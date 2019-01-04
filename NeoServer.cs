using System;
using System.Threading.Tasks;
using Neo.Core.Communication;
using Neo.Core.Networking;

namespace Neo.Server
{
    internal class NeoServer : BaseServer
    {
        //public override void OnConnect(string clientId) { }
        public override async Task OnConnect(Client client) {
            Clients.Add(client);
        }

        public override async Task OnDisconnect(string clientId, ushort code, string reason, bool wasClean) { }

        public override async Task OnError(string clientId, Exception ex, string message) { }

        public override async Task OnPackage(string clientId, Package package) {
            Console.WriteLine(clientId + ": " + package.Content);
        }
    }
}
