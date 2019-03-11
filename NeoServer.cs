using System;
using System.IO;
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
using WebSocketSharp.Server;

namespace Neo.Server
{
    internal class NeoServer : BaseServer
    {
        private void HandlePackage(Client client, Package package) {
            if (package.Type == PackageType.Debug) {

                Logger.Instance.Log(LogLevel.Debug, $"{client.ClientId}: {package.GetContentTypesafe<object>()}");

            } else if (package.Type == PackageType.GuestLogin) {

                if (!ConfigManager.Instance.Values.GuestsAllowed) {
                    return;
                }

                Authenticator.Authenticate(package.GetContentTypesafe<Identity>(), out var guest);

                guest.Attributes.Add("instance.neo.usertype", "guest");
                guest.Client = client;
                Users.Add(guest);

                Logger.Instance.Log(LogLevel.Info, $"{guest.Identity.Name} (@{guest.Identity.Id}) joined the server.");

                EventService.RaiseEvent(EventType.ServerJoined, new JoinElementEventArgs<BaseServer>(guest, this));

                SendPackageTo(client, new Package(PackageType.LoginResponse, LoginResponsePackageContent.GetSuccessful(guest.Identity)));

            } else if (package.Type == PackageType.MemberLogin) {

                var result = Authenticator.Authenticate(package.GetContentTypesafe<MemberLoginPackageContent>(), out var member);

                switch (result) {
                case AuthenticationResult.UnknownUser:
                    SendPackageTo(client, new Package(PackageType.LoginResponse, LoginResponsePackageContent.GetUnknownUser()));
                    return;
                case AuthenticationResult.IncorrectPassword:
                    SendPackageTo(client, new Package(PackageType.LoginResponse, LoginResponsePackageContent.GetIncorrectPassword()));
                    return;
                case AuthenticationResult.ExistingSession:
                    SendPackageTo(client, new Package(PackageType.LoginResponse, LoginResponsePackageContent.GetUnauthorized()));
                    return;
                }

                if (member.Account.Attributes.ContainsKey("neo.banned") && (bool) member.Account.Attributes["neo.banned"]) {
                    Logger.Instance.Log(LogLevel.Warn, $"{member.Identity.Name} (@{member.Identity.Id}) tried to join the server but is banned.");
                    SendPackageTo(client, new Package(PackageType.LoginResponse, LoginResponsePackageContent.GetUnauthorized()));
                    return;
                }

                member.Client = client;
                Users.Add(member);

                Logger.Instance.Log(LogLevel.Info, $"{member.Identity.Name} (@{member.Identity.Id}) joined the server.");

                EventService.RaiseEvent(EventType.ServerJoined, new JoinElementEventArgs<BaseServer>(member, this));

                SendPackageTo(client, new Package(PackageType.LoginResponse, LoginResponsePackageContent.GetSuccessful(member.Identity, member.Account)));

            } else if (package.Type == PackageType.Meta) {

                SendPackageTo(client, new Package(PackageType.MetaResponse, new ServerMetaResponsePackageContent {
                    GuestsAllowed = ConfigManager.Instance.Values.GuestsAllowed,
                    Name = ConfigManager.Instance.Values.ServerName,
                    RegistrationAllowed = ConfigManager.Instance.Values.RegistrationAllowed
                }));

            } else if (package.Type == PackageType.Input) {

                var user = GetUser(client);
                var data = package.GetContentTypesafe<InputPackageContent>();

                var beforeInputEvent = new Before<InputEventArgs>(new InputEventArgs(user, data.Input));
                EventService.RaiseEvent(EventType.BeforeInput, beforeInputEvent);

                if (!beforeInputEvent.Cancel) {
                    var channel = Channels.Find(_ => _.InternalId.Equals(data.TargetChannel));
                    channel.AddMessage(user, data.Input);
                }

            } else if (package.Type == PackageType.Register) {

                if (!ConfigManager.Instance.Values.RegistrationAllowed) {
                    SendPackageTo(client, new Package(PackageType.LoginResponse, LoginResponsePackageContent.GetUnauthorized()));
                    return;
                }

                var result = Authenticator.Register(package.GetContentTypesafe<RegisterPackageContent>(), out var user);

                switch (result) {
                case AuthenticationResult.EmailInUse:
                    SendPackageTo(client, new Package(PackageType.LoginResponse, LoginResponsePackageContent.GetEmailInUse()));
                    return;
                case AuthenticationResult.IdInUse:
                    SendPackageTo(client, new Package(PackageType.LoginResponse, LoginResponsePackageContent.GetIdInUse()));
                    return;
                }

                if (!user.HasValue) {
                    SendPackageTo(client, new Package(PackageType.LoginResponse, LoginResponsePackageContent.GetUnauthorized()));
                    return;
                }

                var beforeAccountCreateEvent = new Before<CreateElementEventArgs<Account>>(new CreateElementEventArgs<Account>(user.Value.Member, user.Value.Account));
                EventService.RaiseEvent(EventType.BeforeAccountCreate, beforeAccountCreateEvent);

                if (!beforeAccountCreateEvent.Cancel) {
                    Accounts.Add(user.Value.Account);
                    Users.Add(user.Value.Member);
                    user.Value.Member.Client = client;

                    DataProvider.Save();

                    Logger.Instance.Log(LogLevel.Info, $"{user.Value.Member.Identity.Name} (@{user.Value.Member.Identity.Id}) registered and joined the server.");

                    EventService.RaiseEvent(EventType.AccountCreated, new CreateElementEventArgs<Account>(user.Value.Member, user.Value.Account));

                    SendPackageTo(client, new Package(PackageType.LoginResponse, LoginResponsePackageContent.GetSuccessful(user.Value.Member.Identity, user.Value.Account)));
                }

            } else if (package.Type == PackageType.LoginFinished) {

                var user = GetUser(client);

                UserManager.RefreshAccounts();
                GroupManager.RefreshGroups();
                UserManager.RefreshUsers();

                if (user.Attributes.ContainsKey("instance.neo.usertype") && user.Attributes["instance.neo.usertype"].ToString() == "guest") {
                    GroupManager.AddGuestToGroup(user as Guest);
                }

                if (user is Member member && member.Groups.Count == 0) {
                    GroupManager.AddMemberToGroup(member, GroupManager.GetUserGroup());
                }

                EventService.RaiseEvent(EventType.Login, new LoginEventArgs(user));

                user.OpenChannel(ChannelManager.GetMainChannel());

                SendPackageTo(client, new Package(PackageType.KnownPermissionsUpdate, KnownPermissions));

            } else if (package.Type == PackageType.EnterChannel) {

                var user = GetUser(client);
                var data = package.GetContentTypesafe<EnterChannelPackageContent>();
                var channel = Channels.Find(_ => _.InternalId.Equals(data.ChannelId));

                if (channel != null) {
                    var result = user.OpenChannel(channel, data.Password);

                    if (result != ChannelActionResult.Success) {
                        SendPackageTo(client, new Package(PackageType.EnterChannelResponse, new EnterChannelResponsePackageContent(result)));
                    }
                }

            } else if (package.Type == PackageType.OpenSettings) {

                SendPackageTo(client, new Package(PackageType.OpenSettingsResponse, SettingsProvider.OpenSettings(package.GetContentTypesafe<string>())));

            } else if (package.Type == PackageType.EditSettings) {

                var user = GetUser(client);
                var data = package.GetContentTypesafe<EditSettingsPackageContent>();
                var result = SettingsProvider.EditSettings(data.Scope, data.Model, user);

                if (data.Scope != "account") {
                    SendPackageTo(client, new Package(PackageType.EditSettingsResponse, result));
                }

            } else if (package.Type == PackageType.EditProfile) {

                var user = GetUser(client);
                var data = package.GetContentTypesafe<EditProfilePackageContent>();
                var member = user as Member;

                if (data.Key == "name") {
                    user.Identity.Name = data.Value.ToString();
                }

                if (member != null && data.Key != "name") {
                    if (data.Key == "id") {
                        if (data.Value.ToString().StartsWith("Guest-") || Accounts.Any(a => a.Identity.Id.Equals(data.Value.ToString()))) {
                            user.ToTarget().SendPackage(new Package(PackageType.EditProfileResponse, new EditProfileResponsePackageContent(null, null, data)));
                            return;
                        }

                        member.Identity.Id = data.Value.ToString();
                    } else if (data.Key == "email") {
                        if (Accounts.Any(a => a.Email.Equals(data.Value.ToString()))) {
                            user.ToTarget().SendPackage(new Package(PackageType.EditProfileResponse, new EditProfileResponsePackageContent(null, null, data)));
                            return;
                        }

                        member.Account.Email = data.Value.ToString();
                    } else if (data.Key == "password") {
                        // TODO: Check current password and set new one

                        var passwords = JsonConvert.DeserializeObject<string[]>(JsonConvert.SerializeObject(data.Value));
                        if (!member.Account.Password.SequenceEqual(Convert.FromBase64String(passwords[0]))) {
                            user.ToTarget().SendPackage(new Package(PackageType.EditProfileResponse, new EditProfileResponsePackageContent(null, null, data)));
                            return;
                        }

                        member.Account.Password = Convert.FromBase64String(passwords[1]);
                    } else {
                        return;
                    }
                }

                DataProvider.Save();

                if (member != null && data.Key != "name" && data.Key != "id") {
                    EventService.RaiseEvent(EventType.AccountEdited, new EditElementEventArgs<Account>(user, member.Account));
                } else {
                    EventService.RaiseEvent(EventType.IdentityEdited, new EditElementEventArgs<Identity>(user, user.Identity));
                }

                SendPackageTo(client, new Package(PackageType.EditProfileResponse, new EditProfileResponsePackageContent(member?.Account, user.Identity, data)));

                UserManager.RefreshUsers();

            } else if (package.Type == PackageType.CreatePunishment) {

                var user = GetUser(client);
                var data = package.GetContentTypesafe<CreatePunishmentPackageContent>();
                var target = Users.Find(_ => _.InternalId.Equals(data.Target));

                if (!user.IsAuthorized("neo.moderate." + data.Action)) {
                    // TODO: Maybe send error back to client
                    return;
                }

                if (target == null) {
                    return;
                }

                SendPackageTo(target.Client, new Package(PackageType.DisconnectReason, data.Action));

                switch (data.Action) {
                case "kick":
                    target.Client.Socket.Close();

                    break;
                case "ban":
                    target.Client.Socket.Close();

                    if (target is Member member) {
                        member.Account.Attributes["neo.banned"] = true;
                        DataProvider.Save();

                        UserManager.RefreshAccounts();
                    }

                    break;
                }

                // TODO: Maybe add punishment event

            } else if (package.Type == PackageType.CreateChannel) {

                var user = GetUser(client);
                var data = package.GetContentTypesafe<CreateChannelPackageContent>();

                var channel = new Channel {
                    Id = data.Id,
                    Lifetime = data.Lifetime,
                    Limit = data.Limit,
                    Name = data.Name,
                    Password = data.Password
                };

                var beforeChannelCreateEvent = new Before<CreateElementEventArgs<Channel>>(new CreateElementEventArgs<Channel>(user, channel));
                EventService.RaiseEvent(EventType.BeforeChannelCreate, beforeChannelCreateEvent);

                if (!beforeChannelCreateEvent.Cancel) {
                    user.CreateChannel(channel);
                }

                EventService.RaiseEvent(EventType.ChannelCreated, new CreateElementEventArgs<Channel>(user, channel));

            } else if (package.Type == PackageType.CreateGroup) {

                var user = GetUser(client);
                var data = package.GetContentTypesafe<CreateGroupPackageContent>();

                var group = new Group {
                    Id = data.Id,
                    Name = data.Name,
                    SortValue = data.SortValue
                };

                var beforeGroupCreateEvent = new Before<CreateElementEventArgs<Group>>(new CreateElementEventArgs<Group>(user, group));
                EventService.RaiseEvent(EventType.BeforeGroupCreate, beforeGroupCreateEvent);

                if (!beforeGroupCreateEvent.Cancel) {
                    var result = GroupManager.CreateGroup(group, GetUser(client.ClientId));

                    SendPackageTo(client, new Package(PackageType.CreateGroupResponse, result));

                    EventService.RaiseEvent(EventType.GroupCreated, new CreateElementEventArgs<Group>(user, group));
                }

            } else if (package.Type == PackageType.DeleteGroup) {

                var user = GetUser(client);
                var group = Groups.Find(_ => _.InternalId == package.GetContentTypesafe<Guid>());

                if (group == null) {
                    SendPackageTo(client, new Package(PackageType.DeleteGroupResponse, "NotFound"));
                    return;
                }

                var beforeGroupRemoveEvent = new Before<RemoveElementEventArgs<Group>>(new RemoveElementEventArgs<Group>(user, group));
                EventService.RaiseEvent(EventType.BeforeGroupRemove, beforeGroupRemoveEvent);

                if (!beforeGroupRemoveEvent.Cancel) {
                    var result = GroupManager.DeleteGroup(group, user);

                    SendPackageTo(client, new Package(PackageType.DeleteGroupResponse, result));

                    EventService.RaiseEvent(EventType.GroupRemoved, new RemoveElementEventArgs<Group>(user, group));
                }

            } else if (package.Type == PackageType.DeleteChannel) {

                var user = GetUser(client);
                var channel = Channels.Find(_ => _.InternalId == package.GetContentTypesafe<Guid>());

                if (channel == null) {
                    SendPackageTo(client, new Package(PackageType.DeleteChannelResponse, "NotFound"));
                    return;
                }

                var beforeChannelRemoveEvent = new Before<RemoveElementEventArgs<Channel>>(new RemoveElementEventArgs<Channel>(user, channel));
                EventService.RaiseEvent(EventType.BeforeChannelRemove, beforeChannelRemoveEvent);

                if (!beforeChannelRemoveEvent.Cancel) {
                    var result = channel.DeleteChannel(user);

                    SendPackageTo(client, new Package(PackageType.DeleteChannelResponse, result ? "Success" : "NotAllowed"));

                    EventService.RaiseEvent(EventType.ChannelRemoved, new RemoveElementEventArgs<Channel>(user, channel));
                }

            } else if (package.Type == PackageType.DeletePunishment) {

                var user = GetUser(client);
                var account = Accounts.Find(a => a.InternalId.Equals(package.GetContentTypesafe<Guid>()));

                if (account == null) {
                    return;
                }

                if (!user.IsAuthorized("neo.punishments.delete")) {
                    // TODO: Inform user
                    return;
                }

                account.Attributes.Remove("neo.banned");
                DataProvider.Save();

                UserManager.RefreshAccounts();

            } else if (package.Type == PackageType.SetAvatar) {

                var user = GetUser(client);
                var data = package.GetContentTypesafe<AvatarPackageContent>();

                var avatarsPath = Path.Combine(dataPath, @"avatars");
                foreach (var file in new DirectoryInfo(avatarsPath).EnumerateFiles(user.InternalId + ".*")) {
                    file.Delete();
                }

                File.WriteAllBytes(Path.Combine(avatarsPath, user.InternalId + data.FileExtension), data.Avatar);
                data.Avatar = null;

                user.Identity.AvatarFileExtension = data.FileExtension;
                user.Attributes["neo.avatar.updated"] = DateTime.Now;

                EventService.RaiseEvent(EventType.AccountEdited, new EditElementEventArgs<Account>(user, (user as Member).Account));

                UserManager.RefreshAccounts();
                UserManager.RefreshUsers();

            }
        }

        public override async Task OnConnect(Client client, WebSocketSessionManager sessions) {
            Logger.Instance.Log(LogLevel.Debug, $"New connection received from {sessions[client.ClientId].Context.UserEndPoint.Address}.");
            Clients.Add(client);

            EventService.RaiseEvent(EventType.Connected, new ConnectEventArgs(client, sessions[client.ClientId].Context.UserEndPoint.Address));
        }

        public override async Task OnDisconnect(string clientId, ushort code, string reason, bool wasClean) {
            var user = GetUser(clientId);
            var client = Clients.Find(_ => _.ClientId == clientId);

            Clients.Remove(client);

            if (user != null) {
                user.LeaveChannel(ChannelManager.GetMainChannel());

                Users.Remove(user);
                
                if (user is Guest guest) {
                    GroupManager.RemoveGuestFromGroup(guest);
                }

                Logger.Instance.Log(LogLevel.Debug, $"{user.Identity.Name} (@{user.Identity.Id}) left the server.");

                UserManager.RefreshUsers();

                EventService.RaiseEvent(EventType.ServerLeft, new LeaveElementEventArgs<BaseServer>(user, this));
            }

            EventService.RaiseEvent(EventType.Disconnected, new DisconnectEventArgs(client, code, reason, wasClean));
        }

        public override async Task OnError(string clientId, Exception ex, string message) { }

        public override async Task OnPackage(string clientId, Package package) {
            var client = Clients.Find(_ => _.ClientId == clientId);

            var beforePackageReceiveEvent = new Before<ReceiveElementEventArgs<Package>>(new ReceiveElementEventArgs<Package>(client, package));
            EventService.RaiseEvent(EventType.BeforePackageReceive, beforePackageReceiveEvent);

            if (!beforePackageReceiveEvent.Cancel) {
                HandlePackage(client, package);

                EventService.RaiseEvent(EventType.PackageReceived, new ReceiveElementEventArgs<Package>(client, package));
            }
        }
    }
}
