/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Microsoft Corporation. All rights reserved.
 *  Licensed under the MIT License. See License.txt in the project root for license information.
 *--------------------------------------------------------------------------------------------*/
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace VSCodeDebug
{
	internal class Program
	{
		const int DEFAULT_PORT = 4711;

		private static bool trace_requests;
		private static bool trace_responses;
		static string LOG_FILE_PATH = null;

		private static void Main(string[] argv)
		{
			int port = -1;

			// parse command line arguments
			foreach (var a in argv) {
				switch (a) {
				case "--trace":
					trace_requests = true;
					break;
				case "--trace=response":
					trace_requests = true;
					trace_responses = true;
					break;
				case "--server":
					port = DEFAULT_PORT;
					break;
				default:
					if (a.StartsWith("--server=")) {
						if (!int.TryParse(a.Substring("--server=".Length), out port)) {
							port = DEFAULT_PORT;
						}
					}
					else if( a.StartsWith("--log-file=")) {
						LOG_FILE_PATH = a.Substring("--log-file=".Length);
					}
					break;
				}
			}

			if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("mono_debug_logfile")) == false) {
				LOG_FILE_PATH = Environment.GetEnvironmentVariable("mono_debug_logfile");
				trace_requests = true;
				trace_responses = true;
			}

			if (port > 0) {
				// TCP/IP server
				Program.Log("waiting for debug protocol on port " + port);
				RunServer(port);
			} else {
				// stdin/stdout
				Program.Log("waiting for debug protocol on stdin/stdout");
				RunSession(Console.OpenStandardInput(), Console.OpenStandardOutput());
			}
		}

		static TextWriter logFile;

		public static void Log(bool predicate, string format, params object[] data)
		{
			if (predicate)
			{
				Log(format, data);
			}
		}
		
		public static void Log(string format, params object[] data)
		{
			try
			{
				try {
					Console.Error.WriteLine(format, data);
				} catch (FormatException) {
					Console.Error.WriteLine (format);
				}

				if (LOG_FILE_PATH != null)
				{
					if (logFile == null)
					{
						logFile = File.CreateText(LOG_FILE_PATH);
					}

					string msg = string.Format(format, data);
					logFile.WriteLine(string.Format("{0} {1}", DateTime.UtcNow.ToLongTimeString(), msg));
				}
			}
			catch (Exception ex)
			{
				if (LOG_FILE_PATH != null)
				{
					try
					{
						File.WriteAllText(LOG_FILE_PATH + ".err", ex.ToString());
					}
					catch
					{
					}
				}

				throw;
			}
		}

		private static void RunSession(Stream inputStream, Stream outputStream)
		{
			MonoDebugSession debugSession = new MonoDebugSession(inputStream, outputStream);
			debugSession.Protocol.DispatcherError += (e, error) => Program.Log (error.Exception.ToString ());
			debugSession.Protocol.LogMessage += (sender, e) => Log (e.Message, e.Category);
			debugSession.Run();

			if (logFile != null)
			{
				logFile.Flush();
				logFile.Close();
				logFile = null;
			}
		}

		private static void RunServer(int port)
		{
			Thread listenThread = new Thread(() =>
			{
				TcpListener listener = new TcpListener(IPAddress.Parse("127.0.0.1"), port);
				listener.Start();

				while (true)
				{
					Socket clientSocket = listener.AcceptSocket();
					Thread clientThread = new Thread(() =>
					{
						Console.WriteLine("Accepted connection");

						using (Stream stream = new NetworkStream(clientSocket))
						{
							var adapter = new MonoDebugSession(stream, stream);
							adapter.Protocol.LogMessage += (sender, e) => Console.WriteLine(e.Message);
							adapter.Protocol.DispatcherError += (sender, e) =>
							{
								Console.Error.WriteLine(e.Exception.Message);
							};
							adapter.Run();
							adapter.Protocol.WaitForReader();

							adapter = null;
						}

						Console.WriteLine("Connection closed");
					});

					clientThread.Name = "DebugServer connection thread";
					clientThread.Start();
				}
			});

			listenThread.Name = "DebugServer listener thread";
			listenThread.Start();
			listenThread.Join();
		}
	}
}
