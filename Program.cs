using System;

namespace Neo.Server
{
    internal class Program
    {
        private static readonly NeoServer server = new NeoServer();

        internal static void Main(string[] args) {
            server.Register();
            server.Start();
            Console.ReadLine();
            server.Stop();
        }
    }
}
