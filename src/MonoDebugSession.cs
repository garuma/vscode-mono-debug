/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Microsoft Corporation. All rights reserved.
 *  Licensed under the MIT License. See License.txt in the project root for license information.
 *--------------------------------------------------------------------------------------------*/
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Linq;
using System.Net;
using Mono.Debugging.Client;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Thread = Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages.Thread;
using Newtonsoft.Json.Linq;

namespace VSCodeDebug
{
	public class MonoDebugSession : DebugAdapterBase
	{
		private const string MONO = "mono";
		private readonly string[] MONO_EXTENSIONS = new String[] {
			".cs", ".csx",
			".cake",
			".fs", ".fsi", ".ml", ".mli", ".fsx", ".fsscript",
			".hx"
		};
		private const int MAX_CHILDREN = 100;
		private const int MAX_CONNECTION_ATTEMPTS = 10;
		private const int CONNECTION_ATTEMPT_INTERVAL = 500;

		private AutoResetEvent _resumeEvent = new AutoResetEvent(false);
		private bool _debuggeeExecuting = false;
		private readonly object _lock = new object();
		private Mono.Debugging.Soft.SoftDebuggerSession _session;
		private volatile bool _debuggeeKilled = true;
		private ProcessInfo _activeProcess;
		private Mono.Debugging.Client.StackFrame _activeFrame;
		private long _nextBreakpointId = 0;
		private SortedDictionary<long, BreakEvent> _breakpoints;
		private List<Catchpoint> _catchpoints;
		private DebuggerSessionOptions _debuggerSessionOptions;

		private System.Diagnostics.Process _process;
		private Handles<ObjectValue[]> _variableHandles;
		private Handles<Mono.Debugging.Client.StackFrame> _frameHandles;
		private ObjectValue _exception;
		private Dictionary<int, Thread> _seenThreads = new Dictionary<int, Thread>();
		private bool _attachMode = false;
		private bool _terminated = false;
		private bool _stderrEOF = true;
		private bool _stdoutEOF = true;

		private bool _clientLinesStartAt1 = true;
		private bool _clientPathsAreURI = true;

		public MonoDebugSession (Stream input, Stream output)
		{
			_variableHandles = new Handles<ObjectValue[]>();
			_frameHandles = new Handles<Mono.Debugging.Client.StackFrame>();
			_seenThreads = new Dictionary<int, Thread>();

			_debuggerSessionOptions = new DebuggerSessionOptions {
				EvaluationOptions = EvaluationOptions.DefaultOptions
			};

			_session = new Mono.Debugging.Soft.SoftDebuggerSession();
			_session.Breakpoints = new BreakpointStore();

			_breakpoints = new SortedDictionary<long, BreakEvent>();
			_catchpoints = new List<Catchpoint>();

			DebuggerLoggingService.CustomLogger = new CustomLogger();

			_session.ExceptionHandler = ex => {
				return true;
			};

			_session.LogWriter = (isStdErr, text) => {
			};

			_session.TargetStopped += (sender, e) => {
				Stopped();
				Protocol.SendEvent(CreateStoppedEvent(StoppedEvent.ReasonValue.Step, e.Thread));
				_resumeEvent.Set();
			};

			_session.TargetHitBreakpoint += (sender, e) => {
				Stopped();
				Protocol.SendEvent(CreateStoppedEvent(StoppedEvent.ReasonValue.Breakpoint, e.Thread));
				_resumeEvent.Set();
			};

			_session.TargetExceptionThrown += (sender, e) => {
				Stopped();
				var ex = DebuggerActiveException();
				if (ex != null) {
					_exception = ex.Instance;
					Protocol.SendEvent(CreateStoppedEvent(StoppedEvent.ReasonValue.Exception, e.Thread, ex.Message));
				}
				_resumeEvent.Set();
			};

			_session.TargetUnhandledException += (sender, e) => {
				Stopped ();
				var ex = DebuggerActiveException();
				if (ex != null) {
					_exception = ex.Instance;
					Protocol.SendEvent(CreateStoppedEvent(StoppedEvent.ReasonValue.Exception, e.Thread, ex.Message));
				}
				_resumeEvent.Set();
			};

			_session.TargetStarted += (sender, e) => {
				_activeFrame = null;
			};

			_session.TargetReady += (sender, e) => {
				_activeProcess = _session.GetProcesses().SingleOrDefault();
			};

			_session.TargetExited += (sender, e) => {

				DebuggerKill();

				_debuggeeKilled = true;

				Terminate("target exited");

				_resumeEvent.Set();
			};

			_session.TargetInterrupted += (sender, e) => {
				_resumeEvent.Set();
			};

			_session.TargetEvent += (sender, e) => {
			};

			_session.TargetThreadStarted += (sender, e) => {
				int tid = (int)e.Thread.Id;
				lock (_seenThreads) {
					_seenThreads[tid] = new Thread(tid, e.Thread.Name);
				}
				Protocol.SendEvent(new ThreadEvent(ThreadEvent.ReasonValue.Started, tid));
			};

			_session.TargetThreadStopped += (sender, e) => {
				int tid = (int)e.Thread.Id;
				lock (_seenThreads) {
					_seenThreads.Remove(tid);
				}
				Protocol.SendEvent(new ThreadEvent(ThreadEvent.ReasonValue.Exited, tid));
			};

			_session.OutputWriter = (isStdErr, text) => {
				Protocol.SendEvent(new OutputEvent(text)
				{
					Category = isStdErr ? OutputEvent.CategoryValue.Stderr : OutputEvent.CategoryValue.Stdout
				});
			};


			this.InitializeProtocolClient(input, output);
		}

		public void Run() => Protocol.Run();

		protected override InitializeResponse HandleInitializeRequest(InitializeArguments arguments)
		{
			_clientLinesStartAt1 = arguments.LinesStartAt1.GetValueOrDefault(true);
			_clientPathsAreURI = arguments.PathFormat == InitializeArguments.PathFormatValue.Uri;

			this.Protocol.SendEvent(new InitializedEvent());

			return new InitializeResponse()
			{
				// This debug adapter does not need the configurationDoneRequest.
				SupportsConfigurationDoneRequest = false,

				// This debug adapter does not support function breakpoints.
				SupportsFunctionBreakpoints = false,

				// This debug adapter doesn't support conditional breakpoints.
				SupportsConditionalBreakpoints = false,

				// This debug adapter does not support a side effect free evaluate request for data hovers.
				SupportsEvaluateForHovers = false,

				// This debug adapter does not support exception breakpoint filters
				ExceptionBreakpointFilters = new List<ExceptionBreakpointsFilter>()
			};
		}

		protected override LaunchResponse HandleLaunchRequest(LaunchArguments arguments)
		{
			_attachMode = false;

			if (arguments.ConfigurationProperties.TryGetValue ("__exceptionOptions", out var exceptionOptions))
				SetExceptionBreakpoints(exceptionOptions);

			// validate argument 'program'
			string programPath = getString(arguments.ConfigurationProperties, "program");
			if (programPath == null) {
				throw new ProtocolException ("Property 'program' is missing or empty.", new Message (3001, "Property 'program' is missing or empty."));
			}
			programPath = ConvertClientPathToDebugger(programPath);
			if (!File.Exists(programPath) && !Directory.Exists(programPath)) {
				throw new ProtocolException ("Program does not exist.", new Message (3002, "Program does not exist."));
			}

			// validate argument 'cwd'
			var workingDirectory = getString (arguments.ConfigurationProperties, "cwd");
			if (workingDirectory != null) {
				workingDirectory = workingDirectory.Trim();
				if (workingDirectory.Length == 0) {
					throw new ProtocolException ("Property 'cwd' is empty.", new Message (3003, "Property 'cwd' is empty."));
				}
				workingDirectory = ConvertClientPathToDebugger(workingDirectory);
				if (!Directory.Exists(workingDirectory)) {
					throw new ProtocolException ("Working directory does not exist", new Message (3004, "Working directory does not exist"));
				}
			}

			// validate argument 'runtimeExecutable'
			var runtimeExecutable = getString (arguments.ConfigurationProperties, "runtimeExecutable");
			if (runtimeExecutable != null) {
				runtimeExecutable = runtimeExecutable.Trim();
				if (runtimeExecutable.Length == 0) {
					throw new ProtocolException ("Property 'runtimeExecutable' is empty.", new Message (3005, "Property 'runtimeExecutable' is empty."));
				}
				runtimeExecutable = ConvertClientPathToDebugger(runtimeExecutable);
				if (!File.Exists(runtimeExecutable)) {
					throw new ProtocolException ("Runtime executable does not exist.", new Message (3006, "Runtime executable does not exist."));
				}
			}


			// validate argument 'env'
			Dictionary<string, object> env = null;
			if (arguments.ConfigurationProperties.TryGetValue ("env", out var envConfigurationProp)) {
				var environmentVariables = envConfigurationProp as JObject;
				if (environmentVariables != null) {
					env = new Dictionary<string, object>();
					foreach (var entry in environmentVariables) {
						env.Add((string)entry.Key, (string)entry.Value);
					}
					if (env.Count == 0) {
						env = null;
					}
				}
			}

			const string host = "127.0.0.1";
			int port = Utilities.FindFreePort(55555);

			string mono_path = runtimeExecutable;
			if (mono_path == null) {
				if (!Utilities.IsOnPath(MONO)) {
					throw new ProtocolException ("Can't find runtime on PATH.", new Message (3011, "Can't find runtime on PATH."));
				}
				mono_path = MONO;     // try to find mono through PATH
			}


			var cmdLine = new List<String>();

			bool debug = !arguments.NoDebug.GetValueOrDefault (false);
			if (debug) {
				cmdLine.Add("--debug");
				cmdLine.Add(String.Format("--debugger-agent=transport=dt_socket,server=y,address={0}:{1}", host, port));
			}

			// add 'runtimeArgs'
			if (arguments.ConfigurationProperties.TryGetValue ("runtimeArgs", out var runtimeArgsConfigurationProp)) {
				string[] runtimeArguments = runtimeArgsConfigurationProp.ToObject<string[]>();
				if (runtimeArguments != null && runtimeArguments.Length > 0) {
					cmdLine.AddRange(runtimeArguments);
				}
			}

			// add 'program'
			if (workingDirectory == null) {
				// if no working dir given, we use the direct folder of the executable
				workingDirectory = Path.GetDirectoryName(programPath);
				cmdLine.Add(Path.GetFileName(programPath));
			}
			else {
				// if working dir is given and if the executable is within that folder, we make the program path relative to the working dir
				cmdLine.Add(Utilities.MakeRelativePath(workingDirectory, programPath));
			}

			// add 'args'
			if (arguments.ConfigurationProperties.TryGetValue ("args", out var argsConfigurationProp)) {
				string[] configArgs = argsConfigurationProp.ToObject<string[]>();
				if (configArgs != null && configArgs.Length > 0) {
					cmdLine.AddRange(configArgs);
				}
			}

			// what console?
			var console = getString(arguments.ConfigurationProperties, "console", null);
			if (console == null) {
				// continue to read the deprecated "externalConsole" attribute
				bool externalConsole = getBool(arguments.ConfigurationProperties, "externalConsole", false);
				if (externalConsole) {
					console = "externalTerminal";
				}
			}

			if (console == "externalTerminal" || console == "integratedTerminal") {

				cmdLine.Insert(0, mono_path);

				var resp = Protocol.SendClientRequestSync (new RunInTerminalRequest (workingDirectory, cmdLine.ToList ()) {
					Env = env,
					Kind = console == "integratedTerminal" ? RunInTerminalArguments.KindValue.Integrated : RunInTerminalArguments.KindValue.External,
				});

			} else { // internalConsole

				_process = new System.Diagnostics.Process();
				_process.StartInfo.CreateNoWindow = true;
				_process.StartInfo.UseShellExecute = false;
				_process.StartInfo.WorkingDirectory = workingDirectory;
				_process.StartInfo.FileName = mono_path;
				_process.StartInfo.Arguments = Utilities.ConcatArgs(cmdLine.ToArray());

				_stdoutEOF = false;
				_process.StartInfo.RedirectStandardOutput = true;
				_process.OutputDataReceived += (object sender, System.Diagnostics.DataReceivedEventArgs e) => {
					if (e.Data == null) {
						_stdoutEOF = true;
					}
					SendOutput(OutputEvent.CategoryValue.Stdout, e.Data);
				};

				_stderrEOF = false;
				_process.StartInfo.RedirectStandardError = true;
				_process.ErrorDataReceived += (object sender, System.Diagnostics.DataReceivedEventArgs e) => {
					if (e.Data == null) {
						_stderrEOF = true;
					}
					SendOutput(OutputEvent.CategoryValue.Stderr, e.Data);
				};

				_process.EnableRaisingEvents = true;
				_process.Exited += (object sender, EventArgs e) => {
					Terminate("runtime process exited");
				};

				if (env != null) {
					// we cannot set the env vars on the process StartInfo because we need to set StartInfo.UseShellExecute to true at the same time.
					// instead we set the env vars on MonoDebug itself because we know that MonoDebug lives as long as a debug session.
					foreach (var entry in env) {
						System.Environment.SetEnvironmentVariable(entry.Key, (string)entry.Value);
					}
				}

				var cmd = string.Format("{0} {1}", mono_path, _process.StartInfo.Arguments);
				SendOutput(OutputEvent.CategoryValue.Console, cmd);

				try {
					_process.Start();
					_process.BeginOutputReadLine();
					_process.BeginErrorReadLine();
				}
				catch (Exception) {
					throw new ProtocolException ("Can't launch terminal", new Message (3012, "Can't launch terminal"));
				}
			}

			if (debug) {
				Connect(IPAddress.Parse(host), port);
			}

			if (_process == null && !debug) {
				// we cannot track mono runtime process so terminate this session
				Terminate("cannot track mono runtime");
			}

			return new LaunchResponse ();
		}

		protected override AttachResponse HandleAttachRequest(AttachArguments arguments)
		{
			_attachMode = true;

			if (arguments.ConfigurationProperties.TryGetValue ("__exceptionOptions", out var exceptionOptions))
				SetExceptionBreakpoints(exceptionOptions);

			// validate argument 'address'
			var host = getString(arguments.ConfigurationProperties, "address");
			if (host == null) {
				throw new ProtocolException("Property 'address' is missing or empty.", new Message(3007, "Property 'address' is missing or empty."));
			}

			// validate argument 'port'
			var port = getInt(arguments.ConfigurationProperties, "port", -1);
			if (port == -1) {
				throw new ProtocolException("Property 'port' is missing.", new Message(3008, "Property 'port' is missing."));
			}

			IPAddress address = Utilities.ResolveIPAddress(host);
			if (address == null) {
				throw new ProtocolException("Invalid address", new Message(3013, "Invalid address '{address}'.") {
					Variables = new Dictionary<string, object> {
						{ "address", address }
					}
				});
			}

			Connect(address, port);

			return new AttachResponse();
		}

		protected override DisconnectResponse HandleDisconnectRequest(DisconnectArguments arguments)
		{
			if (_attachMode) {

				lock (_lock) {
					if (_session != null) {
						_debuggeeExecuting = true;
						_breakpoints.Clear();
						_session.Breakpoints.Clear();
						_session.Continue();
						_session = null;
					}
				}

			} else {
				// Let's not leave dead Mono processes behind...
				if (_process != null) {
					_process.Kill();
					_process = null;
				} else {
					PauseDebugger();
					DebuggerKill();

					while (!_debuggeeKilled) {
						System.Threading.Thread.Sleep(10);
					}
				}
			}

			return new DisconnectResponse();
		}

		protected override ContinueResponse HandleContinueRequest(ContinueArguments arguments)
		{
			WaitForSuspend();
			lock (_lock) {
				if (_session != null && !_session.IsRunning && !_session.HasExited) {
					_session.Continue();
					_debuggeeExecuting = true;
				}
			}
			return new ContinueResponse();
		}

		protected override NextResponse HandleNextRequest(NextArguments arguments)
		{
			WaitForSuspend();
			lock (_lock) {
				if (_session != null && !_session.IsRunning && !_session.HasExited) {
					_session.NextLine();
					_debuggeeExecuting = true;
				}
			}
			return new NextResponse();
		}

		protected override StepInResponse HandleStepInRequest(StepInArguments arguments)
		{
			WaitForSuspend();
			lock (_lock) {
				if (_session != null && !_session.IsRunning && !_session.HasExited) {
					_session.StepLine();
					_debuggeeExecuting = true;
				}
			}
			return new StepInResponse();
		}

		protected override StepOutResponse HandleStepOutRequest(StepOutArguments arguments)
		{
			WaitForSuspend();
			lock (_lock) {
				if (_session != null && !_session.IsRunning && !_session.HasExited) {
					_session.Finish();
					_debuggeeExecuting = true;
				}
			}
			return new StepOutResponse();
		}

		protected override PauseResponse HandlePauseRequest(PauseArguments arguments)
		{
			PauseDebugger();
			return new PauseResponse();
		}

		protected override SetExceptionBreakpointsResponse HandleSetExceptionBreakpointsRequest(SetExceptionBreakpointsArguments arguments)
		{
			SetExceptionBreakpoints(arguments.ExceptionOptions);
			return new SetExceptionBreakpointsResponse();
		}

		protected override SetBreakpointsResponse HandleSetBreakpointsRequest(SetBreakpointsArguments arguments)
		{
			string path = null;
			if (arguments.Source != null) {
				string p = arguments.Source.Path;
				if (p != null && p.Trim().Length > 0) {
					path = p;
				}
			}
			if (path == null) {
				throw new ProtocolException("setBreakpoints: property 'source' is empty or misformed", new Message(3010, "setBreakpoints: property 'source' is empty or misformed"));
			}
			path = ConvertClientPathToDebugger(path);

			if (!HasMonoExtension(path)) {
				return new SetBreakpointsResponse();
			}

			var clientLines = arguments.Lines;
			HashSet<int> lin = new HashSet<int>();
			for (int i = 0; i < clientLines.Count; i++) {
				lin.Add(ConvertClientLineToDebugger(clientLines[i]));
			}

			// find all breakpoints for the given path and remember their id and line number
			var bpts = new List<Tuple<int, int>>();
			foreach (var be in _breakpoints) {
				var bp = be.Value as Mono.Debugging.Client.Breakpoint;
				if (bp != null && bp.FileName == path) {
					bpts.Add(new Tuple<int,int>((int)be.Key, (int)bp.Line));
				}
			}

			HashSet<int> lin2 = new HashSet<int>();
			foreach (var bpt in bpts) {
				if (lin.Contains(bpt.Item2)) {
					lin2.Add(bpt.Item2);
				}
				else {
					// Program.Log("cleared bpt #{0} for line {1}", bpt.Item1, bpt.Item2);

					BreakEvent b;
					if (_breakpoints.TryGetValue(bpt.Item1, out b)) {
						_breakpoints.Remove(bpt.Item1);
						_session.Breakpoints.Remove(b);
					}
				}
			}

			for (int i = 0; i < clientLines.Count; i++) {
				var l = ConvertClientLineToDebugger(clientLines[i]);
				if (!lin2.Contains(l)) {
					var id = _nextBreakpointId++;
					_breakpoints.Add(id, _session.Breakpoints.Add(path, l));
					// Program.Log("added bpt #{0} for line {1}", id, l);
				}
			}

			var breakpoints = new List<Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages.Breakpoint>();
			foreach (var l in clientLines) {
				breakpoints.Add(new Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages.Breakpoint(true) { Line = l });
			}

			return new SetBreakpointsResponse(breakpoints);
		}

		protected override StackTraceResponse HandleStackTraceRequest(StackTraceArguments arguments)
		{
			int maxLevels = arguments.Levels.GetValueOrDefault(10);
			int threadReference = arguments.ThreadId;

			WaitForSuspend();

			ThreadInfo thread = DebuggerActiveThread();
			if (thread.Id != threadReference) {
				// Program.Log("stackTrace: unexpected: active thread should be the one requested");
				thread = FindThread(threadReference);
				if (thread != null) {
					thread.SetActive();
				}
			}

			var stackFrames = new List<Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages.StackFrame>();
			int totalFrames = 0;

			var bt = thread.Backtrace;
			if (bt != null && bt.FrameCount >= 0) {

				totalFrames = bt.FrameCount;

				for (var i = 0; i < Math.Min(totalFrames, maxLevels); i++) {

					var frame = bt.GetFrame(i);

					string path = frame.SourceLocation.FileName;

					var hint = Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages.StackFrame.PresentationHintValue.Subtle;
					Source source = null;
					if (!string.IsNullOrEmpty(path)) {
						string sourceName = Path.GetFileName(path);
						if (!string.IsNullOrEmpty(sourceName)) {
							if (File.Exists(path)) {
								source = new Source
								{
									Name = sourceName,
									Path = ConvertDebuggerPathToClient(path),
									SourceReference = 0,
									PresentationHint = Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages.Source.PresentationHintValue.Normal
								};
								hint = Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages.StackFrame.PresentationHintValue.Normal;
							} else {
								source = new Source()
								{
									Name = sourceName,
									Path = null,
									SourceReference = 1000,
									PresentationHint = Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages.Source.PresentationHintValue.Deemphasize
								};
							}
						}
					}

					var frameHandle = _frameHandles.Create(frame);
					string name = frame.SourceLocation.MethodName;
					int line = frame.SourceLocation.Line;
					stackFrames.Add(new Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages.StackFrame(frameHandle, name, ConvertDebuggerLineToClient(line), 0)
					{
						Source = source,
						PresentationHint = hint
					});
				}
			}

			return new StackTraceResponse(stackFrames) { TotalFrames = totalFrames };
		}

		protected override SourceResponse HandleSourceRequest(SourceArguments arguments)
		{
			throw new ProtocolException("No source available", new Message(1020, "No source available"));
		}

		protected override ScopesResponse HandleScopesRequest(ScopesArguments arguments)
		{
			int frameId = arguments.FrameId;
			var frame = _frameHandles.Get(frameId, null);

			var scopes = new List<Scope>();

			if (frame.Index == 0 && _exception != null) {
				scopes.Add(new Scope("Exception", _variableHandles.Create(new ObjectValue[] { _exception }), false));
			}

			var locals = new[] { frame.GetThisReference() }.Concat(frame.GetParameters()).Concat(frame.GetLocalVariables()).Where(x => x != null).ToArray();
			if (locals.Length > 0) {
				scopes.Add(new Scope("Local", _variableHandles.Create(locals), false));
			}

			return new ScopesResponse(scopes);
		}

		protected override VariablesResponse HandleVariablesRequest(VariablesArguments arguments)
		{
			int reference = arguments.VariablesReference;
			if (reference == -1) {
				throw new ProtocolException("variables: property 'variablesReference' is missing", new Message(3009, "variables: property 'variablesReference' is missing"));
			}

			WaitForSuspend();
			var variables = new List<Variable>();

			ObjectValue[] children;
			if (_variableHandles.TryGet(reference, out children)) {
				if (children != null && children.Length > 0) {

					bool more = false;
					if (children.Length > MAX_CHILDREN) {
						children = children.Take(MAX_CHILDREN).ToArray();
						more = true;
					}

					if (children.Length < 20) {
						// Wait for all values at once.
						WaitHandle.WaitAll(children.Select(x => x.WaitHandle).ToArray());
						foreach (var v in children) {
							variables.Add(CreateVariable(v));
						}
					}
					else {
						foreach (var v in children) {
							v.WaitHandle.WaitOne();
							variables.Add(CreateVariable(v));
						}
					}

					if (more) {
						variables.Add(new Variable("...", null, 0));
					}
				}
			}

			return new VariablesResponse(variables);
		}

		protected override ThreadsResponse HandleThreadsRequest(ThreadsArguments arguments)
		{
			var threads = new List<Thread>();
			var process = _activeProcess;
			if (process != null) {
				Dictionary<int, Thread> d;
				lock (_seenThreads) {
					d = new Dictionary<int, Thread>(_seenThreads);
				}
				foreach (var t in process.GetThreads()) {
					int tid = (int)t.Id;
					d[tid] = new Thread(tid, t.Name);
				}
				threads = d.Values.ToList();
			}
			return new ThreadsResponse(threads);
		}

		protected override EvaluateResponse HandleEvaluateRequest(EvaluateArguments arguments)
		{
			string error = null;

			var expression = arguments.Expression;
			if (expression == null) {
				error = "expression missing";
			} else {
				int frameId = arguments.FrameId.GetValueOrDefault(-1);
				var frame = _frameHandles.Get(frameId, null);
				if (frame != null) {
					if (frame.ValidateExpression(expression)) {
						var val = frame.GetExpressionValue(expression, _debuggerSessionOptions.EvaluationOptions);
						val.WaitHandle.WaitOne();

						var flags = val.Flags;
						if (flags.HasFlag(ObjectValueFlags.Error) || flags.HasFlag(ObjectValueFlags.NotSupported)) {
							error = val.DisplayValue;
							if (error.IndexOf("reference not available in the current evaluation context") > 0) {
								error = "not available";
							}
						}
						else if (flags.HasFlag(ObjectValueFlags.Unknown)) {
							error = "invalid expression";
						}
						else if (flags.HasFlag(ObjectValueFlags.Object) && flags.HasFlag(ObjectValueFlags.Namespace)) {
							error = "not available";
						}
						else {
							int handle = 0;
							if (val.HasChildren) {
								handle = _variableHandles.Create(val.GetAllChildren());
							}
							return new EvaluateResponse(val.DisplayValue, handle);
						}
					}
					else {
						error = "invalid expression";
					}
				}
				else {
					error = "no active stackframe";
				}
			}
			throw new ProtocolException("Evaluate request failed", new Message(3014, "Evaluate request failed ({_reason}).")
			{
				Variables = new Dictionary<string, object> { { "_reason", error } }
			});
		}

		//---- private ------------------------------------------

		private void SetExceptionBreakpoints(JToken exceptionOptions)
		{
			var exceptions = exceptionOptions.ToObject<ExceptionOptions[]>();
			SetExceptionBreakpoints(exceptions);
		}

		private void SetExceptionBreakpoints(IEnumerable<ExceptionOptions> exceptions)
		{
			if (exceptions != null) {

				// clear all existig catchpoints
				foreach (var cp in _catchpoints) {
					_session.Breakpoints.Remove(cp);
				}
				_catchpoints.Clear();

				foreach (var exception in exceptions) {
					string exName = null;
					var exBreakMode = exception.BreakMode;

					if (exception.Path != null) {
						var paths = exception.Path;
						var path = paths[0];
						if (path.Names != null) {
							var names = path.Names;
							if (names.Count > 0) {
								exName = names[0];
							}
						}
					}

					if (exName != null && exBreakMode == ExceptionBreakMode.Always) {
						_catchpoints.Add(_session.Breakpoints.AddCatchpoint(exName));
					}
				}
			}
		}

		private void SendOutput(OutputEvent.CategoryValue? category, string data) {
			if (!String.IsNullOrEmpty(data)) {
				if (data[data.Length-1] != '\n') {
					data += '\n';
				}
				Protocol.SendEvent(new OutputEvent(data) { Category = category });
			}
		}

		private void Terminate(string reason) {
			if (!_terminated) {

				// wait until we've seen the end of stdout and stderr
				for (int i = 0; i < 100 && (_stdoutEOF == false || _stderrEOF == false); i++) {
					System.Threading.Thread.Sleep(100);
				}

				Protocol.SendEvent (new TerminatedEvent());

				_terminated = true;
				_process = null;
			}
		}

		private StoppedEvent CreateStoppedEvent(StoppedEvent.ReasonValue reason, ThreadInfo ti, string text = null) => new StoppedEvent(reason)
		{
			ThreadId = (int)ti.Id,
			Text = text
		};

		private ThreadInfo FindThread(int threadReference)
		{
			if (_activeProcess != null) {
				foreach (var t in _activeProcess.GetThreads()) {
					if (t.Id == threadReference) {
						return t;
					}
				}
			}
			return null;
		}

		private void Stopped()
		{
			_exception = null;
			_variableHandles.Reset();
			_frameHandles.Reset();
		}

		private Variable CreateVariable(ObjectValue v)
		{
			var dv = v.DisplayValue;
			if (dv.Length > 1 && dv [0] == '{' && dv [dv.Length - 1] == '}') {
				dv = dv.Substring (1, dv.Length - 2);
			}
			return new Variable(v.Name, dv, v.HasChildren ? _variableHandles.Create(v.GetAllChildren()) : 0)
			{
				Type = v.TypeName
			};
		}

		private bool HasMonoExtension(string path)
		{
			foreach (var e in MONO_EXTENSIONS) {
				if (path.EndsWith(e)) {
					return true;
				}
			}
			return false;
		}

		private static bool getBool(dynamic container, string propertyName, bool dflt = false)
		{
			try {
				return (bool)container[propertyName];
			}
			catch (Exception) {
				// ignore and return default value
			}
			return dflt;
		}

		private static int getInt(Dictionary<string, JToken> args, string propertyName, int dflt = 0)
		{
			try {
				return (int)args[propertyName];
			}
			catch (Exception) {
				// ignore and return default value
			}
			return dflt;
		}

		private static string getString(Dictionary<string, JToken> args, string property, string dflt = null)
		{
			if (!args.ContainsKey (property))
				return dflt;
			var s = args[property].ToObject<string> ();
			if (s == null) {
				return dflt;
			}
			s = s.Trim();
			if (s.Length == 0) {
				return dflt;
			}
			return s;
		}

		//-----------------------

		private void WaitForSuspend()
		{
			if (_debuggeeExecuting) {
				_resumeEvent.WaitOne();
				_debuggeeExecuting = false;
			}
		}

		private ThreadInfo DebuggerActiveThread()
		{
			lock (_lock) {
				return _session == null ? null : _session.ActiveThread;
			}
		}

		private Backtrace DebuggerActiveBacktrace() {
			var thr = DebuggerActiveThread();
			return thr == null ? null : thr.Backtrace;
		}

		private Mono.Debugging.Client.StackFrame DebuggerActiveFrame() {
			if (_activeFrame != null)
				return _activeFrame;

			var bt = DebuggerActiveBacktrace();
			if (bt != null)
				return _activeFrame = bt.GetFrame(0);

			return null;
		}

		private ExceptionInfo DebuggerActiveException() {
			var bt = DebuggerActiveBacktrace();
			return bt == null ? null : bt.GetFrame(0).GetException();
		}

		private void Connect(IPAddress address, int port)
		{
			lock (_lock) {

				_debuggeeKilled = false;

				var args0 = new Mono.Debugging.Soft.SoftDebuggerConnectArgs(string.Empty, address, port) {
					MaxConnectionAttempts = MAX_CONNECTION_ATTEMPTS,
					TimeBetweenConnectionAttempts = CONNECTION_ATTEMPT_INTERVAL
				};

				_session.Run(new Mono.Debugging.Soft.SoftDebuggerStartInfo(args0), _debuggerSessionOptions);

				_debuggeeExecuting = true;
			}
		}

		private void PauseDebugger()
		{
			lock (_lock) {
				if (_session != null && _session.IsRunning)
					_session.Stop();
			}
		}

		private void DebuggerKill()
		{
			lock (_lock) {
				if (_session != null) {

					_debuggeeExecuting = true;

					if (!_session.HasExited)
						_session.Exit();

					_session.Dispose();
					_session = null;
				}
			}
		}

		private int ConvertDebuggerLineToClient(int line)
		{
			return _clientLinesStartAt1 ? line : line - 1;
		}

		private int ConvertClientLineToDebugger(int line)
		{
			return _clientLinesStartAt1 ? line : line + 1;
		}

		private string ConvertDebuggerPathToClient(string path)
		{
			if (_clientPathsAreURI)
			{
				try
				{
					var uri = new System.Uri(path);
					return uri.AbsoluteUri;
				}
				catch
				{
					return null;
				}
			}
			else
			{
				return path;
			}
		}

		private string ConvertClientPathToDebugger(string clientPath)
		{
			if (clientPath == null)
			{
				return null;
			}

			if (_clientPathsAreURI)
			{
				if (Uri.IsWellFormedUriString(clientPath, UriKind.Absolute))
				{
					Uri uri = new Uri(clientPath);
					return uri.LocalPath;
				}
				Program.Log("path not well formed: '{0}'", clientPath);
				return null;
			}
			else
			{
				return clientPath;
			}
		}
	}
}
