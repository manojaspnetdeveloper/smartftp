using Renci.SshNet;
using Serilog;
using SmartAttomTaxroll;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Smartattomtaxroll
{
    internal static class Program
    {

        static void Main(string[] args)
        {
            ParserService service = new ParserService();
            service.OnStart(new string[0]);
        }

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        static void Main1()
        {
            //ServiceBase[] ServicesToRun;
            //ServicesToRun = new ServiceBase[]
            //{
            //    new Service1()
            //};
            //ServiceBase.Run(ServicesToRun);
            Log.Logger = new LoggerConfiguration()
                  .MinimumLevel.Debug()
                  //.WriteTo.Console()
                  //.WriteTo.File("service_ftp_log.txt", rollingInterval: RollingInterval.Day)
                  .CreateLogger();

           
        }
    }
}
