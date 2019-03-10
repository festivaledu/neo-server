using System;
using System.Linq;
using System.Threading.Tasks;
using Neo.Core.Authentication;
using Neo.Core.Authorization;
using Neo.Core.Communication;
using Neo.Core.Communication.Packages;
using Neo.Core.Config;
using Neo.Core.Extensibility;
using Neo.Core.Extensibility.Events;
using Neo.Core.Management;
using Neo.Core.Networking;
using Neo.Core.Shared;
using Newtonsoft.Json;
using WebSocketSharp;
using Logger = Neo.Core.Shared.Logger;
using LogLevel = Neo.Core.Shared.LogLevel;

namespace Neo.Server
{
    internal class NeoServer : BaseServer
    {
        private void HandlePackage(Client client, Package package) {
            if (package.Type == PackageType.Debug) {

                // Console.WriteLine(client.ClientId + ": " + package.GetContentTypesafe<string>());
                SendPackageTo(new Target(client.ClientId), package);

            } else if (package.Type == PackageType.GuestLogin) {

                if (!ConfigManager.Instance.Values.GuestsAllowed) {
                    return;
                }

                Authenticator.Authenticate(package.GetContentTypesafe<Identity>(), out var guest);
                guest.Attributes.Add("instance.neo.usertype", "guest");
                guest.Client = client;
                Users.Add(guest);

                Logger.Instance.Log(LogLevel.Debug, $"{guest.Identity.Name} joined (Id: {guest.Identity.Id})");

                SendPackageTo(client.ClientId, new Package(PackageType.LoginResponse, LoginResponsePackageContent.GetSuccessful(guest.Identity)));
                


            } else if (package.Type == PackageType.MemberLogin) {

                var result = Authenticator.Authenticate(package.GetContentTypesafe<MemberLoginPackageContent>(), out var member);

                switch (result) {
                case AuthenticationResult.UnknownUser:
                    SendPackageTo(client.ClientId, new Package(PackageType.LoginResponse, LoginResponsePackageContent.GetUnknownUser()));
                    return;
                case AuthenticationResult.IncorrectPassword:
                    SendPackageTo(client.ClientId, new Package(PackageType.LoginResponse, LoginResponsePackageContent.GetIncorrectPassword()));
                    return;
                case AuthenticationResult.ExistingSession:
                    SendPackageTo(client.ClientId, new Package(PackageType.LoginResponse, LoginResponsePackageContent.GetUnauthorized()));
                    return;
                }

                if (member.Account.Attributes.ContainsKey("neo.banned") && (bool) member.Account.Attributes["neo.banned"]) {
                    Logger.Instance.Log(LogLevel.Warn, $"{member.Identity.Name} tried to join (Id: {member.Identity.Id}) but is banned.");
                    SendPackageTo(client.ClientId, new Package(PackageType.LoginResponse, LoginResponsePackageContent.GetUnauthorized()));
                    return;
                }

                member.Client = client;
                Users.Add(member);

                Logger.Instance.Log(LogLevel.Debug, $"{member.Identity.Name} joined (Id: {member.Identity.Id})");

                SendPackageTo(client.ClientId, new Package(PackageType.LoginResponse, LoginResponsePackageContent.GetSuccessful(member.Identity, member.Account)));

            } else if (package.Type == PackageType.Meta) {

                SendPackageTo(client.ClientId, new Package(PackageType.MetaResponse, new ServerMetaResponsePackageContent {
                    GuestsAllowed = ConfigManager.Instance.Values.GuestsAllowed,
                    Name = ConfigManager.Instance.Values.ServerName,
                    RegistrationAllowed = ConfigManager.Instance.Values.RegistrationAllowed
                }));

            } else if (package.Type == PackageType.Input) {

                var user = GetUser(client.ClientId);
                var data = package.GetContentTypesafe<InputPackageContent>();

                var beforeInputEvent = new Before<InputEventArgs>(new InputEventArgs(user, data.Input));
                EventService.RaiseEvent(EventType.BeforeInput, beforeInputEvent);
                
                if (!beforeInputEvent.Cancel) {
                    var channel = Channels.Find(c => c.InternalId.Equals(data.TargetChannel));
                    channel.AddMessage(user, data.Input);
                }

            } else if (package.Type == PackageType.Register) {

                if (!ConfigManager.Instance.Values.RegistrationAllowed) {
                    SendPackageTo(client.ClientId, new Package(PackageType.LoginResponse, LoginResponsePackageContent.GetUnauthorized()));
                    return;
                }

                var result = Authenticator.Register(package.GetContentTypesafe<RegisterPackageContent>(), out var user);
                
                switch (result) {
                case AuthenticationResult.EmailInUse:
                    SendPackageTo(client.ClientId, new Package(PackageType.LoginResponse, LoginResponsePackageContent.GetEmailInUse()));
                    return;
                case AuthenticationResult.IdInUse:
                    SendPackageTo(client.ClientId, new Package(PackageType.LoginResponse, LoginResponsePackageContent.GetIdInUse()));
                    return;
                }

                if (!user.HasValue) {
                    SendPackageTo(client.ClientId, new Package(PackageType.LoginResponse, LoginResponsePackageContent.GetUnauthorized()));
                    return;
                }

                // TODO: Maybe raise BeforeAccountCreateEvent
                Accounts.Add(user.Value.account);
                Users.Add(user.Value.member);
                user.Value.member.Client = client;
                
                DataProvider.Save();

                Logger.Instance.Log(LogLevel.Debug, $"{user.Value.member.Identity.Name} registered and joined (Id: {user.Value.member.Identity.Id})");

                SendPackageTo(client.ClientId, new Package(PackageType.LoginResponse, LoginResponsePackageContent.GetSuccessful(user.Value.member.Identity, user.Value.account)));


            } else if (package.Type == PackageType.LoginFinished) {
                var user = GetUser(client.ClientId);

                Target.All.SendPackageTo(new Package(PackageType.AccountListUpdate, Pool.Server.Accounts));

                GroupManager.RefreshGroups();
                UserManager.RefreshUsers();

                if (user.Attributes.ContainsKey("instance.neo.usertype") && user.Attributes["instance.neo.usertype"].ToString() == "guest") {
                    GroupManager.AddGuestToGroup(user as Guest);
                }

                if (user is Member member && member.Groups.Count == 0) {
                    GroupManager.AddMemberToGroup(member, GroupManager.GetUserGroup());
                }

                Logger.Instance.Log(LogLevel.Debug, user.Identity.Name + " tried to join #main: " + user.OpenChannel(ChannelManager.GetMainChannel()));

                user.ToTarget().SendPackageTo(new Package(PackageType.KnownPermissionsUpdate, KnownPermissions));

            } else if (package.Type == PackageType.EnterChannel) {
                var user = GetUser(client.ClientId);
                var data = package.GetContentTypesafe<EnterChannelPackageContent>();
                var channel = Channels.Find(c => c.InternalId.Equals(data.ChannelId));

                if (channel != null) {
                    var result = user.OpenChannel(channel, data.Password);
                    Logger.Instance.Log(LogLevel.Debug, user.Identity.Name + " tried to join " + channel.Name + ": " + result);

                    if (result != ChannelActionResult.Success) {
                        user.ToTarget().SendPackageTo(new Package(PackageType.EnterChannelResponse, new EnterChannelResponsePackageContent(result)));
                    }
                }
            } else if (package.Type == PackageType.OpenSettings) {
                new Target(client.ClientId).SendPackageTo(new Package(PackageType.OpenSettingsResponse, SettingsProvider.OpenSettings(package.GetContentTypesafe<string>())));
            } else if (package.Type == PackageType.EditSettings) {
                var data = package.GetContentTypesafe<EditSettingsPackageContent>();
                var result = SettingsProvider.EditSettings(data.Scope, data.Model);

                if (data.Scope != "account") {
                    new Target(client.ClientId).SendPackageTo(new Package(PackageType.EditSettingsResponse, result));
                }
            } else if (package.Type == PackageType.EditProfile) {
                var data = package.GetContentTypesafe<EditProfilePackageContent>();
                var user = GetUser(client.ClientId);
                var member = user as Member;

                if (data.Key == "name") {
                    user.Identity.Name = data.Value.ToString();
                }

                if (member != null && data.Key != "name") {
                    if (data.Key == "id") {
                        if (data.Value.ToString().StartsWith("Guest-") || Accounts.Any(a => a.Identity.Id.Equals(data.Value.ToString()))) {
                            user.ToTarget().SendPackageTo(new Package(PackageType.EditProfileResponse, new EditProfileResponsePackageContent(null, null, data)));
                            return;
                        }

                        member.Identity.Id = data.Value.ToString();
                    } else if (data.Key == "email") {
                        if (Accounts.Any(a => a.Email.Equals(data.Value.ToString()))) {
                            user.ToTarget().SendPackageTo(new Package(PackageType.EditProfileResponse, new EditProfileResponsePackageContent(null, null, data)));
                            return;
                        }

                        member.Account.Email = data.Value.ToString();
                    } else if (data.Key == "password") {
                        // TODO: Check current password and set new one

                        var passwords = JsonConvert.DeserializeObject<string[]>(JsonConvert.SerializeObject(data.Value));
                        if (!member.Account.Password.SequenceEqual(Convert.FromBase64String(passwords[0]))) {
                            user.ToTarget().SendPackageTo(new Package(PackageType.EditProfileResponse, new EditProfileResponsePackageContent(null, null, data)));
                            return;
                        }

                        member.Account.Password = Convert.FromBase64String(passwords[1]);
                    }
                }

                DataProvider.Save();

                user.ToTarget().SendPackageTo(new Package(PackageType.EditProfileResponse, new EditProfileResponsePackageContent(member?.Account, user.Identity, data)));
                UserManager.RefreshUsers();
            } else if (package.Type == PackageType.CreatePunishment) {
                var data = package.GetContentTypesafe<CreatePunishmentPackageContent>();
                var user = Users.Find(u => u.InternalId.Equals(data.Target));

                if (!GetUser(client.ClientId).IsAuthorized("neo.moderate." + data.Action)) {
                    // TODO: Maybe send error back to client
                    return;
                }

                if (user == null) {
                    return;
                }

                user.ToTarget().SendPackageTo(new Package(PackageType.DisconnectReason, data.Action));

                if (data.Action == "kick") {
                    user.Client.Socket.Close();
                } else if (data.Action == "ban") {
                    user.Client.Socket.Close();

                    if (user is Member member) {
                        member.Account.Attributes["neo.banned"] = true;
                        DataProvider.Save();
                        Target.All.SendPackageTo(new Package(PackageType.AccountListUpdate, Pool.Server.Accounts));
                    }
                }
            } else if (package.Type == PackageType.CreateChannel) {
                var data = package.GetContentTypesafe<CreateChannelPackageContent>();
                var user = GetUser(client.ClientId);

                var channel = new Channel {
                    Id = data.Id,
                    Lifetime = data.Lifetime,
                    Limit = data.Limit,
                    Name = data.Name,
                    Password = data.Password
                };

                user.CreateChannel(channel);
            } else if (package.Type == PackageType.CreateGroup) {
                var data = package.GetContentTypesafe<CreateGroupPackageContent>();

                var result = GroupManager.CreateGroup(new Group {
                    Id = data.Id,
                    Name = data.Name,
                    SortValue = data.SortValue
                }, GetUser(client.ClientId));

                new Target(client.ClientId).SendPackageTo(new Package(PackageType.CreateGroupResponse, result));
            } else if (package.Type == PackageType.DeleteGroup) {
                var group = Groups.Find(g => g.InternalId == package.GetContentTypesafe<Guid>());

                if (group == null) {
                    new Target(client.ClientId).SendPackageTo(new Package(PackageType.DeleteGroupResponse, "NotFound"));
                    return;
                }

                new Target(client.ClientId).SendPackageTo(new Package(PackageType.DeleteGroupResponse, GroupManager.DeleteGroup(group, GetUser(client.ClientId))));
            } else if (package.Type == PackageType.DeleteChannel) {
                var channel = Channels.Find(c => c.InternalId == package.GetContentTypesafe<Guid>());

                if (channel == null) {
                    new Target(client.ClientId).SendPackageTo(new Package(PackageType.DeleteChannelResponse, "NotFound"));
                    return;
                }

                new Target(client.ClientId).SendPackageTo(new Package(PackageType.DeleteChannelResponse, channel.DeleteChannel(GetUser(client.ClientId)) ? "Success" : "NotAllowed"));
            } else if (package.Type == PackageType.DeletePunishment) {
                var account = Accounts.Find(a => a.InternalId.Equals(package.GetContentTypesafe<Guid>()));

                if (account == null) {
                    return;
                }

                account.Attributes.Remove("neo.banned");
                DataProvider.Save();
                Target.All.SendPackageTo(new Package(PackageType.AccountListUpdate, Pool.Server.Accounts));
            }
        }
        
        public override async Task OnConnect(Client client) {

            Logger.Instance.Log(LogLevel.Debug, "New connection received");
            Clients.Add(client);
            await EventService.RaiseEvent(EventType.Connected, new ConnectEventArgs(client));
            
        }

        public override async Task OnDisconnect(string clientId, ushort code, string reason, bool wasClean) {

            var client = Clients.Find(c => c.ClientId == clientId);
            Clients.Remove(client);

            var user = GetUser(clientId);
            if (user != null) {
                user.LeaveChannel(ChannelManager.GetMainChannel());
                Logger.Instance.Log(LogLevel.Debug, $"{user.Identity.Name} left (Id: {user.Identity.Id})");
                Users.Remove(user);

                if (user is Guest guest) {
                    GroupManager.RemoveGuestFromGroup(guest);
                }

                UserManager.RefreshUsers();
            }

            await EventService.RaiseEvent(EventType.Disconnected, new DisconnectEventArgs(client, code, reason, wasClean));

        }

        public override async Task OnError(string clientId, Exception ex, string message) { }

        public override async Task OnPackage(string clientId, Package package) {
            var client = Clients.Find(c => c.ClientId == clientId);

            var beforeReceivePackageEvent = new Before<ReceiveElementEventArgs<Package>>(new ReceiveElementEventArgs<Package>(client, package));
            await EventService.RaiseEvent(EventType.BeforePackageReceive, beforeReceivePackageEvent);

            if (!beforeReceivePackageEvent.Cancel) {
                Logger.Instance.Log(LogLevel.Info, "Package received: " + package.Type);
                HandlePackage(client, package);

                await EventService.RaiseEvent(EventType.PackageReceived, new ReceiveElementEventArgs<Package>(client, package));
            }
        }
    }
}
