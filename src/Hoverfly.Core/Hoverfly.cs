﻿namespace Hoverfly.Core
{
    using System;
    using System.Configuration;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.NetworkInformation;
    using System.Text;
    using System.Threading;

    using Configuration;
    using Logging;
    using Resources;

    public class Hoverfly
    {
        private const int BOOT_TIMEOUT_SECONDS = 10;
        private const int RETRY_BACKOFF_INTERVAL_MS = 100;

        private const int KILL_PROCESS_TIMEOUT = 2;

        private const string HOVERFLY_EXE = "hoverfly.exe";

        private readonly HoverflyConfig _hoverflyConfig;

        private readonly IHoverflyClient _hoverflyClient;

        private readonly ISimulationSource _simulationSource;

        private readonly ILog _logger;

        private readonly HoverflyMode _hoverflyMode;

        private Process _hoverflyProcess;

        public Hoverfly(
            HoverflyMode hoverflyMode,
            HoverflyConfig config = null,
            IHoverflyClient hoverflyClient = null,
            ISimulationSource simulationSource = null,
            ILoggerFactory loggerFactory = null)
        {
            _hoverflyMode = hoverflyMode;

            _hoverflyConfig = config ?? HoverflyConfig.Config();

            _logger = loggerFactory?.Create(this.GetType().Name);

            _hoverflyClient = hoverflyClient ?? new HoverflyClient(
                                                         new Uri($"{_hoverflyConfig.RemoteHost}:{_hoverflyConfig.AdminPort}"),
                                                         _logger);

            _simulationSource = simulationSource ?? new FileSimulationSource(Environment.CurrentDirectory);
        }

        public void Start()
        {
            if (!_hoverflyConfig.IsRemoteInstance)
                StartHoverflyProcess();

            WaitForHoverflyToBecomeHealthy();
            SetProxySystemProperties();
        }

        public void Stop()
        {
            _logger?.Info("Destroying hoverfly process");

            if (_hoverflyProcess == null)
                return;

            _hoverflyProcess.Kill();

            // We can't make sure the Process are really dead hwne we make a call to Kill,
            // so we don't leave stop until we are sure its gone.
            var timeout = DateTime.Now.AddSeconds(KILL_PROCESS_TIMEOUT);

            while (Process.GetProcessesByName("hoverfly").Any() && timeout > DateTime.Now)
                Thread.Sleep(1);

            if (Process.GetProcessesByName("hoverfly").Any())
                throw new TimeoutException("Timeout while waiting for hoverfly process to be closed.");
        }

        public void ImportSimulation(string name)
        {
            _logger?.Info($"Importing simulation data '{name}' to Hoverfly.");

            var simulationData = _simulationSource.GetSimulation(name);

            if (simulationData != null)
                _hoverflyClient.ImportSimulation(simulationData);
        }

        public void ExportSimulation(string name)
        {
            _logger?.Info("Exporting simulation data from Hoverfly.");

            try
            {
                var simulationData = GetSimulation();
                _simulationSource.SaveSimulation(simulationData, name);
            }
            catch (Exception e)
            {
                throw new SimulationExportException($"Can't export simulation, reason: {e}", e);
            }
        }


        public byte[] GetSimulation()
        {
            _logger?.Info("Get simulation data from Hoverfly.");

            return _hoverflyClient.GetSimulation();
        }

        private void SetProxySystemProperties()
        {
            if (_hoverflyMode == HoverflyMode.WEBSERVER)
                return;

            //TODO: Temporary hack to accept all SSL
            ServicePointManager.ServerCertificateValidationCallback = (senderX, certificate, chain, sslPolicyErrors) => true;

            if (_hoverflyConfig.ProxyLocalhost)
            {
                WebRequest.DefaultWebProxy = new WebProxy($"http://localhost:{_hoverflyConfig.ProxyPort}", false);
            }
            else
            {
                WebRequest.DefaultWebProxy = new WebProxy(
                    $"http://localhost:{_hoverflyConfig.ProxyPort}",
                    true,
                    new[] { "local;*.local;169.254/16;*.169.254/16" });
            }
        }

        private void WaitForHoverflyToBecomeHealthy()
        {
            var timeoutDatTime = DateTime.Now.AddSeconds(BOOT_TIMEOUT_SECONDS);

            while (DateTime.Now < timeoutDatTime)
            {
                if (_hoverflyClient.IsHealthy())
                    return;

                Thread.Sleep(RETRY_BACKOFF_INTERVAL_MS);
            }

            throw new TimeoutException($"Hoverfly has not become healthy in '{BOOT_TIMEOUT_SECONDS}' seconds");
        }

        private void StartHoverflyProcess()
        {
            VerifyPortNotInUse(_hoverflyConfig.ProxyPort);
            VerifyPortNotInUse(_hoverflyConfig.AdminPort);

            var hoverflyPath = GetHoverflyPath();

            _logger?.Info($"Start hoverfly from path '{hoverflyPath}'");

            var processInfo = new ProcessStartInfo(hoverflyPath, GetHoverflyArgumentsBasedOnMode())
            {
                WorkingDirectory = _hoverflyConfig.HoverflyBasePath,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            _hoverflyProcess = Process.Start(processInfo);
        }

        private string GetHoverflyPath()
        {
            var hoverfileBasePath = string.IsNullOrWhiteSpace(_hoverflyConfig.HoverflyBasePath) ?
                                           Environment.CurrentDirectory :
                                           _hoverflyConfig.HoverflyBasePath;

            var result = Directory.GetFiles(hoverfileBasePath, HOVERFLY_EXE, SearchOption.AllDirectories);

            if (result.Any())
                return result.First();

            throw new FileNotFoundException($"Can't find the file '{HOVERFLY_EXE}' file in the current directory '{hoverfileBasePath}' or is sub-folders.");
        }

        private string GetHoverflyArgumentsBasedOnMode()
        {
            var arguments = new StringBuilder();

            switch (_hoverflyMode)
            {
                case HoverflyMode.CAPTURE:
                    arguments.Append(" -capture ");
                    break;
                case HoverflyMode.WEBSERVER:
                    arguments.Append(" -webserver ");
                    break;
                case HoverflyMode.SIMULATE:
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            arguments.Append(" -db memory ");
            arguments.Append($" -pp {_hoverflyConfig.ProxyPort} ");
            arguments.Append($" -ap {_hoverflyConfig.AdminPort} ");

            return arguments.ToString();
        }

        private static void VerifyPortNotInUse(int port)
        {
            var ipGlobalProperties = IPGlobalProperties.GetIPGlobalProperties();
            var tcpConnInfoArray = ipGlobalProperties.GetActiveTcpListeners();

            if (tcpConnInfoArray.Any(endpoint => endpoint.Port == port))
            {
                throw new ConfigurationErrorsException($"Port '{port}' is already in use by other application, please use another one");
            }
        }
    }
}