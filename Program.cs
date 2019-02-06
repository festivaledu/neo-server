using System;
using System.IO;
using Neo.Core.Extensibility;

namespace Neo.Server
{
    internal class Program
    {
        private static readonly NeoServer server = new NeoServer();

        internal static void Main(string[] args) {
            server.Initialize(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json"), Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"data\"));
            
            server.Start();

            Console.ReadLine();
            server.Stop();

            Console.ReadLine();
        }
    }
}
