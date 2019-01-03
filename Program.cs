using System;
using System.IO;
using Neo.Core.Config;

namespace Neo.Server
{
    internal class Program
    {
        private static readonly NeoServer server = new NeoServer();

        internal static void Main(string[] args) {
            ConfigManager.Instance.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json"));

            server.Register();
            server.Start();
            Console.ReadLine();
            server.Stop();
        }
    }
}
