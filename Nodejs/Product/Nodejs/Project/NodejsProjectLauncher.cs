﻿//*********************************************************//
//    Copyright (c) Microsoft. All rights reserved.
//    
//    Apache 2.0 License
//    
//    You may obtain a copy of the License at
//    http://www.apache.org/licenses/LICENSE-2.0
//    
//    Unless required by applicable law or agreed to in writing, software 
//    distributed under the License is distributed on an "AS IS" BASIS, 
//    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or 
//    implied. See the License for the specific language governing 
//    permissions and limitations under the License.
//
//*********************************************************//

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web;
using System.Windows.Forms;
using Microsoft.NodejsTools.Debugger;
using Microsoft.NodejsTools.Debugger.DebugEngine;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudioTools;
using Microsoft.VisualStudioTools.Project;
using Microsoft.NodejsTools.TypeScript;

namespace Microsoft.NodejsTools.Project {
    class NodejsProjectLauncher : IProjectLauncher {
        private readonly NodejsProjectNode _project;
        private int? _testServerPort;

        public NodejsProjectLauncher(NodejsProjectNode project) {
            _project = project;

            var portNumber = _project.GetProjectProperty(NodejsConstants.NodejsPort);
            int portNum;
            if (Int32.TryParse(portNumber, out portNum)) {
                _testServerPort = portNum;
            }
        }

        #region IProjectLauncher Members

        public int LaunchProject(bool debug) {
            NodejsPackage.Instance.Logger.LogEvent(Logging.NodejsToolsLogEvent.Launch, debug ? 1 : 0);
            return Start(ResolveStartupFile(), debug);
        }

        public int LaunchFile(string file, bool debug) {
            NodejsPackage.Instance.Logger.LogEvent(Logging.NodejsToolsLogEvent.Launch, debug ? 1 : 0);
            return Start(file, debug);
        }

        private int Start(string file, bool debug) {
            string nodePath = GetNodePath();
            if (nodePath == null) {
                Nodejs.ShowNodejsNotInstalled();
                return VSConstants.S_OK;
            }

            bool startBrowser = ShouldStartBrowser();

            if (debug) {
                StartWithDebugger(file);
            } else {
                var psi = new ProcessStartInfo();
                psi.UseShellExecute = false;

                psi.FileName = nodePath;
                psi.Arguments = GetFullArguments(file);
                psi.WorkingDirectory = _project.GetWorkingDirectory();

                string webBrowserUrl = GetFullUrl();
                Uri uri = null;
                if (!String.IsNullOrWhiteSpace(webBrowserUrl)) {
                    uri = new Uri(webBrowserUrl);

                    psi.EnvironmentVariables["PORT"] = uri.Port.ToString();
                }

                foreach (var nameValue in GetEnvironmentVariables()) {
                    psi.EnvironmentVariables[nameValue.Key] = nameValue.Value;
                }

                var process = NodeProcess.Start(
                    psi,
                    NodejsPackage.Instance.GeneralOptionsPage.WaitOnAbnormalExit,
                    NodejsPackage.Instance.GeneralOptionsPage.WaitOnNormalExit
                );

                if (startBrowser && uri != null) {
                    OnPortOpenedHandler.CreateHandler(
                        uri.Port,
                        shortCircuitPredicate: () => process.HasExited,
                        action: () => {
                            VsShellUtilities.OpenBrowser(webBrowserUrl, (uint)__VSOSPFLAGS.OSP_LaunchNewBrowser);
                        }
                    );
                }
            }
            return VSConstants.S_OK;
        }

        private string GetFullArguments(string file, bool includeNodeArgs = true) {
            string res = String.Empty;
            if (includeNodeArgs) {
                var nodeArgs = _project.GetProjectProperty(NodejsConstants.NodeExeArguments);
                if (!String.IsNullOrWhiteSpace(nodeArgs)) {
                    res = nodeArgs + " ";
                }
            }
            res += "\"" + file + "\"";
            var scriptArgs = _project.GetProjectProperty(NodejsConstants.ScriptArguments);
            if (!String.IsNullOrWhiteSpace(scriptArgs)) {
                res += " " + scriptArgs;
            }
            return res;
        }

        private string GetNodePath() {
            var overridePath = _project.GetProjectProperty(NodejsConstants.NodeExePath);
            if (!String.IsNullOrWhiteSpace(overridePath)) {
                return overridePath;
            }
            return Nodejs.NodeExePath;
        }

        #endregion

        private string GetFullUrl() {
            var host = _project.GetProjectProperty(NodejsConstants.LaunchUrl);

            try {
                return GetFullUrl(host, TestServerPort);
            } catch (UriFormatException) {
                var output = OutputWindowRedirector.GetGeneral(NodejsPackage.Instance);
                output.WriteErrorLine(SR.GetString(SR.ErrorInvalidLaunchUrl, host));
                output.ShowAndActivate();
                return string.Empty;
            }
        }

        internal static string GetFullUrl(string host, int port) {
            UriBuilder builder;
            Uri uri;
            if (Uri.TryCreate(host, UriKind.Absolute, out uri)) {
                builder = new UriBuilder(uri);
            } else {
                builder = new UriBuilder();
                builder.Scheme = Uri.UriSchemeHttp;
                builder.Host = "localhost";
                builder.Path = host;
            }

            builder.Port = port;

            return builder.ToString();
        }

        private string TestServerPortString {
            get {
                if (!_testServerPort.HasValue) {
                    _testServerPort = GetFreePort();
                }
                return _testServerPort.Value.ToString(CultureInfo.InvariantCulture);
            }
        }

        private int TestServerPort {
            get {
                if (!_testServerPort.HasValue) {
                    _testServerPort = GetFreePort();
                }
                return _testServerPort.Value;
            }
        }

        /// <summary>
        /// Default implementation of the "Start Debugging" command.
        /// </summary>
        private void StartWithDebugger(string startupFile) {
            VsDebugTargetInfo dbgInfo = new VsDebugTargetInfo();
            dbgInfo.cbSize = (uint)Marshal.SizeOf(dbgInfo);

            if (SetupDebugInfo(ref dbgInfo, startupFile)) {
                LaunchDebugger(_project.Site, dbgInfo);
            }
        }


        private void LaunchDebugger(IServiceProvider provider, VsDebugTargetInfo dbgInfo) {
            if (!Directory.Exists(UnquotePath(dbgInfo.bstrCurDir))) {
                MessageBox.Show(String.Format("Working directory \"{0}\" does not exist.", dbgInfo.bstrCurDir), "Node.js Tools for Visual Studio");
            } else if (!File.Exists(UnquotePath(dbgInfo.bstrExe))) {
                MessageBox.Show(String.Format("Interpreter \"{0}\" does not exist.", dbgInfo.bstrExe), "Node.js Tools for Visual Studio");
            } else if (DoesProjectSupportDebugging()) {
                VsShellUtilities.LaunchDebugger(provider, dbgInfo);
            }
        }

        private static string UnquotePath(string p) {
            if (p.StartsWith("\"") && p.EndsWith("\"")) {
                return p.Substring(1, p.Length - 2);
            }
            return p;
        }

        private bool DoesProjectSupportDebugging() {
            var typeScriptOutFile = _project.GetProjectProperty("TypeScriptOutFile");
            if (!string.IsNullOrEmpty(typeScriptOutFile)) {
                return MessageBox.Show(
                    "This TypeScript project has 'Combine Javascript output into file' option enabled. This option is not supported by NTVS debugger, " +
                    "and may result in erratic behavior of breakpoints, stepping, and debug tool windows. Are you sure you want to start debugging?",
                    SR.ProductName,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning
                    ) == DialogResult.Yes;
            }

            return true;
        }

        private void AppendOption(ref VsDebugTargetInfo dbgInfo, string option, string value) {
            if (!String.IsNullOrWhiteSpace(dbgInfo.bstrOptions)) {
                dbgInfo.bstrOptions += ";";
            }

            dbgInfo.bstrOptions += option + "=" + HttpUtility.UrlEncode(value);
        }

        /// <summary>
        /// Sets up debugger information.
        /// </summary>
        private bool SetupDebugInfo(ref VsDebugTargetInfo dbgInfo, string startupFile) {
            dbgInfo.dlo = DEBUG_LAUNCH_OPERATION.DLO_CreateProcess;

            dbgInfo.bstrExe = GetNodePath();
            dbgInfo.bstrCurDir = _project.GetWorkingDirectory();
            dbgInfo.bstrArg = GetFullArguments(startupFile, includeNodeArgs: false);    // we need to supply node args via options
            dbgInfo.bstrRemoteMachine = null;
            var nodeArgs = _project.GetProjectProperty(NodejsConstants.NodeExeArguments);
            if (!String.IsNullOrWhiteSpace(nodeArgs)) {
                AppendOption(ref dbgInfo, AD7Engine.InterpreterOptions, nodeArgs);
            }

            var url = GetFullUrl();
            if (ShouldStartBrowser() && !String.IsNullOrWhiteSpace(url)) {
				AppendOption(ref dbgInfo, AD7Engine.WebBrowserUrl, url);
				string browserExecutable;
				string browserArguments;
				if (ShouldOverrideDefaultBrowser(out browserExecutable, out browserArguments)) {
					AppendOption(ref dbgInfo, AD7Engine.BrowserExecutable, browserExecutable);
					if (browserArguments == null) browserArguments = "";
					if (browserArguments.Contains("%1")) {
						browserArguments = browserArguments.Replace("%1", url);
					}
					else {
						browserArguments += (" " + url);
					}
					AppendOption(ref dbgInfo, AD7Engine.BrowserArguments, browserArguments);
				}
            }

            var debuggerPort = _project.GetProjectProperty(NodejsConstants.DebuggerPort);
            if (!String.IsNullOrWhiteSpace(debuggerPort)) {
                AppendOption(ref dbgInfo, AD7Engine.DebuggerPort, debuggerPort);
            }

            if (NodejsPackage.Instance.GeneralOptionsPage.WaitOnAbnormalExit) {
                AppendOption(ref dbgInfo, AD7Engine.WaitOnAbnormalExitSetting, "true");
            }

            if (NodejsPackage.Instance.GeneralOptionsPage.WaitOnNormalExit) {
                AppendOption(ref dbgInfo, AD7Engine.WaitOnNormalExitSetting, "true");
            }

            dbgInfo.fSendStdoutToOutputWindow = 0;

            StringDictionary env = new StringDictionary();
            if (!String.IsNullOrWhiteSpace(url)) {
                Uri webUrl = new Uri(url);
                env["PORT"] = webUrl.Port.ToString();
            }

            foreach (var nameValue in GetEnvironmentVariables()) {
                env[nameValue.Key] = nameValue.Value;
            }

            if (env.Count > 0) {
                // add any inherited env vars
                var variables = Environment.GetEnvironmentVariables();
                foreach (var key in variables.Keys) {
                    string strKey = (string)key;
                    if (!env.ContainsKey(strKey)) {
                        env.Add(strKey, (string)variables[key]);
                    }
                }

                //Environemnt variables should be passed as a
                //null-terminated block of null-terminated strings. 
                //Each string is in the following form:name=value\0
                StringBuilder buf = new StringBuilder();
                foreach (DictionaryEntry entry in env) {
                    buf.AppendFormat("{0}={1}\0", entry.Key, entry.Value);
                }
                buf.Append("\0");
                dbgInfo.bstrEnv = buf.ToString();
            }

            // Set the Node  debugger
            dbgInfo.clsidCustom = AD7Engine.DebugEngineGuid;
            dbgInfo.grfLaunch = (uint)__VSDBGLAUNCHFLAGS.DBGLAUNCH_StopDebuggingOnEnd;
            return true;
        }

        private bool ShouldStartBrowser() {
            var startBrowser = _project.GetProjectProperty(NodejsConstants.StartWebBrowser);
            bool fStartBrowser;
            if (!String.IsNullOrEmpty(startBrowser) &&
                Boolean.TryParse(startBrowser, out fStartBrowser)) {
                return fStartBrowser;
            }

            return true;
        }

		private bool ShouldOverrideDefaultBrowser(out string browserExecutable, out string browserArguments) {
			browserExecutable = null;
			browserArguments = null;

			var overrideDefaultBrowser = _project.GetProjectProperty(NodejsConstants.OverrideDefaultBrowser);
			bool fOverrideDefaultBrowser;
			if (!String.IsNullOrEmpty(overrideDefaultBrowser) &&
				Boolean.TryParse(overrideDefaultBrowser, out fOverrideDefaultBrowser)) {
				browserExecutable = _project.GetProjectProperty(NodejsConstants.BrowserExecutable);
				browserArguments = _project.GetProjectProperty(NodejsConstants.BrowserArguments);

				if (fOverrideDefaultBrowser && !String.IsNullOrEmpty(browserExecutable)) {
					return true;
				}
			}

			return false;
		}

        private IEnumerable<KeyValuePair<string, string>> GetEnvironmentVariables() {
            var envVars = _project.GetProjectProperty(NodejsConstants.Environment);
            if (envVars != null) {
                foreach (var envVar in envVars.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) {
                    var nameValue = envVar.Split(new[] { '=' }, 2);
                    if (nameValue.Length == 2) {
                        yield return new KeyValuePair<string, string>(nameValue[0], nameValue[1]);
                    }
                }
            }
        }

        private static int GetFreePort() {
            return Enumerable.Range(new Random().Next(1200, 2000), 60000).Except(
                from connection in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections()
                select connection.LocalEndPoint.Port
            ).First();
        }

        private string ResolveStartupFile() {
            string startupFile = _project.GetStartupFile();
            if (string.IsNullOrEmpty(startupFile)) {
                throw new ApplicationException("Please select a startup file to launch by right-clicking the file in Solution Explorer and selecting 'Set as Node.js Startup File' or by modifying your configuration in project properties.");
            }

            if (TypeScriptHelpers.IsTypeScriptFile(startupFile)) {
                startupFile = TypeScriptHelpers.GetTypeScriptBackedJavaScriptFile(_project, startupFile);
            }
            return startupFile;
        }
    }

    internal class OnPortOpenedHandler {

        class OnPortOpenedInfo {
            public readonly int Port;
            public readonly TimeSpan? Timeout;
            public readonly int Sleep;
            public readonly Func<bool> ShortCircuitPredicate;
            public readonly Action Action;
            public readonly DateTime StartTime;

            public OnPortOpenedInfo(
                int port,
                int? timeout = null,
                int? sleep = null,
                Func<bool> shortCircuitPredicate = null,
                Action action = null
            ) {
                Port = port;
                if (timeout.HasValue) {
                    Timeout = TimeSpan.FromMilliseconds(Convert.ToDouble(timeout));
                }
                Sleep = sleep ?? 500;                                   // 1/2 second sleep
                ShortCircuitPredicate = shortCircuitPredicate ?? (() => false);
                Action = action ?? (() => { });
                StartTime = System.DateTime.Now;
            }
        }

        internal static void CreateHandler(
            int port,
            int? timeout = null,
            int? sleep = null,
            Func<bool> shortCircuitPredicate = null,
            Action action = null
        ) {
            ThreadPool.QueueUserWorkItem(
                OnPortOpened,
                new OnPortOpenedInfo(
                    port,
                    timeout,
                    sleep,
                    shortCircuitPredicate,
                    action
                )
            );
        }

        private static void OnPortOpened(object infoObj) {
            var info = (OnPortOpenedInfo)infoObj;

            using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)) {
                socket.Blocking = true;
                try {
                    while (true) {
                        // Short circuit
                        if (info.ShortCircuitPredicate()) {
                            return;
                        }

                        // Try connect
                        try {
                            socket.Connect(IPAddress.Loopback, info.Port);
                            break;
                        } catch {
                            // Connect failure
                            // Fall through
                        }

                        // Timeout
                        if (info.Timeout.HasValue && (System.DateTime.Now - info.StartTime) >= info.Timeout) {
                            break;
                        }

                        // Sleep
                        System.Threading.Thread.Sleep(info.Sleep);
                    }
                } finally {
                    socket.Close();
                }
            }

            // Launch browser (if not short-circuited)
            if (!info.ShortCircuitPredicate()) {
                info.Action();
            }
        }
    }
}
