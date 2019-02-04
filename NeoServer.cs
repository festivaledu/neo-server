using System;
using System.Threading.Tasks;
using Neo.Core.Authentication;
using Neo.Core.Communication;
using Neo.Core.Communication.Packages;
using Neo.Core.Config;
using Neo.Core.Extensibility;
using Neo.Core.Extensibility.Events;
using Neo.Core.Networking;
using Neo.Core.Shared;

namespace Neo.Server
{
    internal class NeoServer : BaseServer
    {
        private async Task HandlePackage(Client client, Package package) {
            if (package.Type == PackageType.Debug) {

                Console.WriteLine(client.ClientId + ": " + package.GetContentTypesafe<string>());
                SendPackageTo(new Target(client.ClientId), package);

            } else if (package.Type == PackageType.GuestLogin) {

                if (!ConfigManager.Instance.Values.GuestsAllowed) {
                    // TODO: Guests not allowed
                    return;
                }

                Authenticator.Authenticate(package.GetContentTypesafe<GuestLoginPackageContent>(), out var guest);
                guest.Client = client;
                Users.Add(guest);

                SendPackageTo(client.ClientId, new Package(PackageType.LoginResponse, LoginResponsePackageContent.GetSuccessful(guest.Identity)));

            } else if (package.Type == PackageType.MemberLogin) {

                var result = Authenticator.Authenticate(package.GetContentTypesafe<MemberLoginPackageContent>(), out var member);

                if (result != AuthenticationResult.Success) {
                    // TODO: Something went wrong on login
                    return;
                }

                member.Client = client;
                Users.Add(member);
                
            } else if (package.Type == PackageType.Meta) {

                SendPackageTo(client.ClientId, new Package(PackageType.MetaResponse, new ServerMetaPackageContent {
                    GuestsAllowed = ConfigManager.Instance.Values.GuestsAllowed,
                    Name = ConfigManager.Instance.Values.ServerName,
                    RegistrationAllowed = ConfigManager.Instance.Values.RegistrationAllowed
                }));

            } else if (package.Type == PackageType.Input) {

                var input = package.GetContentTypesafe<string>();

                // BUG: THIS SHALL BE DONE

                SendPackageTo(Target.All.Remove(client.ClientId), new Package(PackageType.Message, new {
                    identity = new Identity { Id = client.ClientId, Name = "Norbert Roqualla" },
                    message = input,
                    timestamp = DateTime.Now,
                    messageType = "received"
                }));

                SendPackageTo(new Target(client.ClientId), new Package(PackageType.Message, new {
                    identity = new Identity { Id = client.ClientId, Name = "Norbert Roqualla" },
                    message = input,
                    timestamp = DateTime.Now,
                    messageType = "sent"
                }));
            } else if (package.Type == PackageType.Register) {

                if (!ConfigManager.Instance.Values.RegistrationAllowed) {
                    // TODO: Registration not allowed
                    return;
                }

                var result = Authenticator.Register(package.GetContentTypesafe<RegisterPackageContent>(), out var user);

                if (result != AuthenticationResult.Success || !user.HasValue) {
                    // TODO: Something went wrong on registering
                    return;
                }
                
                // TODO: Maybe raise BeforeAccountCreateEvent
                Accounts.Add(user.Value.account);
                Users.Add(user.Value.member);

            }
        }
        
        public override async Task OnConnect(Client client) {
            
            Clients.Add(client);
            await EventService.RaiseEvent(EventType.Connected, new ConnectEventArgs(client));
            
        }

        public override async Task OnDisconnect(string clientId, ushort code, string reason, bool wasClean) {

            var client = Clients.Find(c => c.ClientId == clientId);
            Clients.Remove(client);
            await EventService.RaiseEvent(EventType.Disconnected, new DisconnectEventArgs(client, code, reason, wasClean));

        }

        public override async Task OnError(string clientId, Exception ex, string message) { }

        public override async Task OnPackage(string clientId, Package package) {
            var client = Clients.Find(c => c.ClientId == clientId);

            var beforeReceivePackageEvent = new Before<ReceiveElementEventArgs<Package>>(new ReceiveElementEventArgs<Package>(client, package));
            //await EventService.RaiseEvent(EventType.BeforePackageReceive, beforeReceivePackageEvent);

            if (!beforeReceivePackageEvent.Cancel) {
                await HandlePackage(client, package);

                //await EventService.RaiseEvent(EventType.PackageReceived, new ReceiveElementEventArgs<Package>(client, package));
            }
        }
    }
}
