﻿using Microsoft.VisualStudio.Services.Agent.Listener.Configuration;
using Microsoft.VisualStudio.Services.Agent.Util;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Microsoft.VisualStudio.Services.Agent.Listener
{
    public static class Program
    {
        private static Tracing s_trace;

        public static int Main(string[] args)
        {
            return MainAsync(args).GetAwaiter().GetResult();
        }


        public async static Task<int> MainAsync(string[] args)
        {
            switch (Constants.Agent.Platform)
            {
                case Constants.OSPlatform.Linux:
                    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    {
                        Console.WriteLine(StringUtil.Loc("NotLinux"));
                        return 1;
                    }
                    break;
                case Constants.OSPlatform.OSX:
                    if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    {
                        Console.WriteLine(StringUtil.Loc("NotOSX"));
                        return 1;
                    }
                    break;
                case Constants.OSPlatform.Windows:
                    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        Console.WriteLine(StringUtil.Loc("NotWindows"));
                        return 1;
                    }
                    break;
            }

            using (HostContext context = new HostContext("Agent"))
            using (var term = context.GetService<ITerminal>())
            {
                int rc = 0;
                try
                {
                    s_trace = context.GetTrace("AgentProcess");
                    s_trace.Info($"Version: {Constants.Agent.Version}");
                    s_trace.Info($"Commit: {BuildConstants.Source.CommitHash}");

                    //
                    // TODO (bryanmac): Need VsoAgent.exe compat shim for SCM
                    //                  That shim will also provide a compat arg parse 
                    //                  and translate / to -- etc...
                    //
                    CommandLineParser parser = new CommandLineParser(context);
                    parser.Parse(args);
                    s_trace.Info("Arguments parsed");

                    IAgent agent = context.GetService<IAgent>();
                    using (agent.TokenSource = new CancellationTokenSource())
                    {
                        rc = await agent.ExecuteCommand(parser);
                    }
                }
                catch (Exception e)
                {
                    if (!(e is OperationCanceledException))
                    {
                        Console.Error.WriteLine(StringUtil.Format("An error occured.  {0}", e.Message));
                    }
                    s_trace.Error(e);
                    rc = 1;
                }

                return rc;
            }
        }
    }
}
