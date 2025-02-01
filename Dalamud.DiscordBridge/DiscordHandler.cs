using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.DiscordBridge.Model;
using Dalamud.DiscordBridge.XivApi;
using Dalamud.DiscordBridgeFork.API;
using Dalamud.DiscordBridgeFork.Model;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using Discord;
using Discord.Rest;
using Discord.Webhook;
using Discord.WebSocket;
using Lumina.Text;
using NetStone;
using NetStone.Model.Parseables.Character;
using NetStone.Search.Character;

namespace Dalamud.DiscordBridge
{
    public class DiscordHandler : IDisposable
    {
        static readonly IPluginLog Logger = Service.Logger;

        private readonly DuplicateFilter duplicateFilter;
        
        private readonly DiscordSocketClient socketClient;
        private readonly SpecialCharsHandler specialChars;

        public bool IsConnected => this.socketClient.ConnectionState == ConnectionState.Connected;
        public ulong UserId => this.socketClient.CurrentUser.Id;

        private static readonly ConcurrentDictionary<string, LodestoneCNPlayer> CachedResponses = new();

        /// <summary>
        /// Defines if the bot has connected and verified that it has the correct permissions
        /// </summary>
        public DiscordState State { get; private set; } = DiscordState.None;

        private readonly DiscordBridgePlugin plugin;

        /// <summary>
        /// Chat types that are set when used the "all" setting.
        /// </summary>
        private static readonly XivChatType[] DefaultChatTypes =
        [
            XivChatType.Say,
            XivChatType.Shout,
            XivChatType.Yell,
            XivChatType.Party,
            XivChatType.CrossParty,
            XivChatType.PvPTeam,
            XivChatType.TellIncoming,
            XivChatType.Alliance,
            XivChatType.FreeCompany,
            XivChatType.Ls1,
            XivChatType.Ls2,
            XivChatType.Ls3,
            XivChatType.Ls4,
            XivChatType.Ls5,
            XivChatType.Ls6,
            XivChatType.Ls7,
            XivChatType.Ls8,
            XivChatType.NoviceNetwork,
            XivChatType.CustomEmote,
            XivChatType.StandardEmote,
            XivChatType.CrossLinkShell1,
            XivChatType.CrossLinkShell2,
            XivChatType.CrossLinkShell3,
            XivChatType.CrossLinkShell4,
            XivChatType.CrossLinkShell5,
            XivChatType.CrossLinkShell6,
            XivChatType.CrossLinkShell7,
            XivChatType.CrossLinkShell8,
            XivChatType.Echo,
            XivChatType.SystemMessage,
        ];

        /// <summary>
        /// Embed color signalling that everything is fine.
        /// </summary>
        private const int EmbedColorFine = 0x478CFF;

        /// <summary>
        /// Embed color signalling that everything is bad.
        /// </summary>
        private const int EmbedColorError = 0xD10303;

        /// <summary>
        /// The asynchronous message queue that is responsible for sending messages in order.
        /// </summary>
        public readonly DiscordMessageQueue MessageQueue;

        private LodestoneCN lodestoneClient;

        public DiscordHandler(DiscordBridgePlugin plugin)
        {
            this.plugin = plugin;

            this.specialChars = new SpecialCharsHandler();

            this.MessageQueue = new DiscordMessageQueue(this.plugin);

            Logger.Debug("BEFORE DiscordSocketClient");
            this.socketClient = new DiscordSocketClient(new DiscordSocketConfig
            {
                MessageCacheSize = 20, // hold onto the last 20 messages per channel in cache for duplicate checks
                GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.GuildMessages | GatewayIntents.GuildWebhooks | GatewayIntents.MessageContent,
                WebSocketProvider = DiscordWebSocketProvider.Instance,
                RestClientProvider = DiscordRsetClientProvider.Instance
            });
            Logger.Debug("AFTER DiscordSocketClient");
            this.socketClient.Ready += SocketClientOnReady;
            this.socketClient.MessageReceived += SocketClientOnMessageReceived;
            
            this.duplicateFilter = new DuplicateFilter(this.plugin, this.socketClient);
        }

        public async Task Start()
        {

            if (string.IsNullOrEmpty(this.plugin.Config.DiscordToken))
            {
                this.State = DiscordState.TokenInvalid;

                Logger.Error("Token empty, cannot start bot.");
                return;
            }

            try
            {
                await this.socketClient.LoginAsync(TokenType.Bot, this.plugin.Config.DiscordToken);
                await this.socketClient.StartAsync();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Token invalid, cannot start bot.");
            }

            this.MessageQueue.Start();

            lodestoneClient = new LodestoneCN();

            Logger.Debug("DiscordHandler START!!");
        }

        private Task SocketClientOnReady()
        {
            this.State = DiscordState.Ready;
            this.specialChars.TryFindEmote(this.socketClient);

            Logger.Verbose("DiscordHandler READY!!");
            
            return Task.CompletedTask;
        }

        public async Task SetOnlinePresence()
        {
            try
            {
                await this.socketClient.SetStatusAsync(UserStatus.Online);
            }
            catch (Exception e)
            {
                Logger.Error(e, "Failed to set online status.");
            }
            
        }

        public async Task SetIdlePresence()
        {
            try
            {
                await this.socketClient.SetStatusAsync(UserStatus.Idle);
            }
            catch (Exception e)
            {
                Logger.Error(e, "Failed to set idle status.");
            }

        }

        private async Task SocketClientOnMessageReceived(SocketMessage message)
        {
            if (message.Author.IsBot || message.Author.IsWebhook)
                return;

            var args = message.Content.Split();

            // if it doesn't start with the bot prefix, ignore it.
            if (!args[0].StartsWith(this.plugin.Config.DiscordBotPrefix))
                return;

            /*
            // this is only needed for debugging purposes.
            foreach (var s in args)
            {
                Logger.Verbose(s);
            }
            */

            Logger.Verbose("Received command: {0}", args[0]);

            try
            {
                if (args[0] == this.plugin.Config.DiscordBotPrefix + "send" && await EnsureOwner(message.Author, message.Channel))
                {
                    // Are there parameters?
                    if (args.Length == 1)
                    {
                        await SendGenericEmbed(message.Channel,
                            $"要使用此功能，你需要指定对话内容。",
                            "Error", EmbedColorError);

                        return;
                    }
                    string sendMsg = string.Join(" ", args.Skip(1));
                    //判断arg1不是任何channel，则补充默认channel
                    this.plugin.Config.ChannelDefaultKindConfigs.TryGetValue(message.Channel.Id, out var config);
                    if (config != null)
                    {
                        string buildNewMsg = "";
                        if (!args[1].StartsWith("/"))
                        {
                            buildNewMsg = $"/{config.ChatType.GetSlug()}";
                            if (config.ChatType == XivChatType.TellOutgoing)
                            {
                                buildNewMsg = $"{buildNewMsg} {config.TellTargetStr}";
                            }
                        }
                        sendMsg = $"{buildNewMsg} {sendMsg}";
                    }
                    else if (!this.plugin.Config.ChannelDefaultKindConfigNotice.TryGetValue(message.Channel.Id, out var notice) || !notice)
                    {
                        await SendGenericEmbed(message.Channel,
                            $"此Discord频道没有设置默认消息发送的聊天类型。请使用 ``{this.plugin.Config.DiscordBotPrefix}setsendkind <kind>`` 命令设置默认发送的聊天类型。\n没有默认类型时，请小心发送，慎防错频。\n这条消息只会显示一次。",
                            "警告", EmbedColorError);
                        this.plugin.Config.ChannelDefaultKindConfigNotice[message.Channel.Id] = true;
                    }
                    Logger.Verbose("Sending message: {0}", sendMsg);
                    //将arg[1]与之后的参数合并为一个字符串
                    DiscordBridgePlugin.Plugin.SendMessage(sendMsg);
                    return;
                }

                if (args[0] == this.plugin.Config.DiscordBotPrefix + "setsendkind" && await EnsureOwner(message.Author, message.Channel))
                {
                    // Are there parameters?
                    if (args.Length == 1)
                    {
                        await SendGenericEmbed(message.Channel,
                            $"要使用此功能，你需要指定聊天类型。\n使用 ``{this.plugin.Config.DiscordBotPrefix}help`` 命令获取更多信息。",
                            "Error", EmbedColorError);

                        return;
                    }
                    XivChatType xivChatType = XivChatTypeExtensions.GetBySlug(args[1]);
                    if (xivChatType == XivChatType.None)
                    {
                        await SendGenericEmbed(message.Channel,
                            $"无法找到聊天类型。\n使用 ``{this.plugin.Config.DiscordBotPrefix}help`` 命令获取更多信息。",
                            "Error", EmbedColorError);

                        return;
                    }
                    string tellTargetStr = "";
                    if (xivChatType == XivChatType.TellOutgoing)
                    {
                        if (args.Length < 3)
                        {
                            await SendGenericEmbed(message.Channel,
                                $"对于tell，你需要指定私聊对象。\n例如`{this.plugin.Config.DiscordBotPrefix}setsendkind tell aaa@柔风海湾`。",
                                "Error", EmbedColorError);
                            return;
                        }
                        tellTargetStr = args[2];
                    }
                    this.plugin.Config.ChannelDefaultKindConfigs[message.Channel.Id] = new DefaultMsgKindConfig(xivChatType, tellTargetStr);
                    this.plugin.Config.Save();
                    await SendGenericEmbed(message.Channel,
                        $"OK! 当前Discord频道将被设置为默认使用 **{XivChatTypeExtensions.GetBySlug(args[1]).GetFancyName()}** 聊天类型发送消息。\n"
                        + $"当你不想使用默认聊天类型时，你可以使用例如 ``{this.plugin.Config.DiscordBotPrefix}send /say msg`` 来发送消息。",
                        "默认消息类型", EmbedColorFine);
                    return;
                }

                if (args[0] == this.plugin.Config.DiscordBotPrefix + "setchannel" &&
                    await EnsureOwner(message.Author, message.Channel))
                {
                    // Are there parameters?
                    if (args.Length == 1)
                    {
                        await SendGenericEmbed(message.Channel,
                            $"要使用此功能，你需要指定对话类型。\n使用 ``{this.plugin.Config.DiscordBotPrefix}help`` 命令获取更多信息。",
                            "Error", EmbedColorError);

                        return;
                    }

                    var kinds = args[1].Split(',').Select(x => x.ToLower());

                    // Is there any chat type that's not recognized?
                    if (kinds
                        .Any(x =>
                        XivChatTypeExtensions.TypeInfoDict.All(y => y.Value.Slug != x) && x != "any"))
                    {
                        Logger.Verbose("无法找到类型");
                        await SendGenericEmbed(message.Channel,
                            $"One or more of the chat kinds you specified could not be found.\nCheck the ``{this.plugin.Config.DiscordBotPrefix}help`` command for more information.",
                            "Error", EmbedColorError);

                        return;
                    }

                    if (!this.plugin.Config.ChannelConfigs.TryGetValue(message.Channel.Id, out var config))
                        config = new DiscordChannelConfig();

                    foreach (var selectedKind in kinds)
                    {
                        Logger.Verbose(selectedKind);

                        if (selectedKind == "any")
                        {
                            config.SetUnique(DefaultChatTypes);
                        }
                        else if (selectedKind == "tell")
                        {
                            config.SetUnique(XivChatType.TellOutgoing);
                            config.SetUnique(XivChatType.TellIncoming);
                        }
                        else if (selectedKind == "p")
                        {
                            config.SetUnique(XivChatType.Party);
                            config.SetUnique(XivChatType.CrossParty);
                        }
                        else
                        {
                            var chatType = XivChatTypeExtensions.GetBySlug(selectedKind);
                            config.SetUnique(chatType);
                        }
                    }

                    this.plugin.Config.ChannelConfigs[message.Channel.Id] = config;
                    this.plugin.Config.Save();

                    await SendGenericEmbed(message.Channel,
                        $"OK! This channel has been set to receive the following chat kinds:\n\n```\n{config.ChatTypes.Select(x => $"{x.GetFancyName()}").Aggregate((x, y) => x + "\n" + y)}```",
                        "Chat kinds set", EmbedColorFine);

                    return;
                }

                if (args[0] == this.plugin.Config.DiscordBotPrefix + "unsetchannel" &&
                    await EnsureOwner(message.Author, message.Channel))
                {
                    // Are there parameters?
                    if (args.Length == 1)
                    {
                        await SendGenericEmbed(message.Channel,
                            $"You need to specify some chat kinds to use.\nCheck the ``{this.plugin.Config.DiscordBotPrefix}help`` command for more information.",
                            "Error", EmbedColorError);

                        return;
                    }

                    var kinds = args[1].Split(',').Select(x => x.ToLower());

                    // Is there any chat type that's not recognized?
                    if (kinds.Any(x =>
                        XivChatTypeExtensions.TypeInfoDict.All(y => y.Value.Slug != x) && x != "any"))
                    {
                        await SendGenericEmbed(message.Channel,
                            $"One or more of the chat kinds you specified could not be found.\nCheck the ``{this.plugin.Config.DiscordBotPrefix}help`` command for more information.",
                            "Error", EmbedColorError);

                        return;
                    }

                    if (!this.plugin.Config.ChannelConfigs.TryGetValue(message.Channel.Id, out var config))
                        config = new DiscordChannelConfig();

                    foreach (var selectedKind in kinds)
                    {
                        if (selectedKind == "any")
                        {
                            config.UnsetUnique(DefaultChatTypes);
                        }
                        else if (selectedKind == "tell")
                        {
                            config.UnsetUnique(XivChatType.TellOutgoing);
                            config.UnsetUnique(XivChatType.TellIncoming);
                        }
                        else if (selectedKind == "p")
                        {
                            config.UnsetUnique(XivChatType.Party);
                            config.UnsetUnique(XivChatType.CrossParty);
                        }
                        else
                        {
                            var chatType = XivChatTypeExtensions.GetBySlug(selectedKind);
                            config.UnsetUnique(chatType);
                        }
                    }

                    this.plugin.Config.ChannelConfigs[message.Channel.Id] = config;
                    this.plugin.Config.Save();

                    if (config.ChatTypes.Count == 0)
                    {
                        await SendGenericEmbed(message.Channel,
                        $"All chat kinds have been removed from this channel.",
                        "Chat Kinds unset", EmbedColorFine);
                    }
                    await SendGenericEmbed(message.Channel,
                        $"OK! This channel will still receive the following chat kinds:\n\n```\n{config.ChatTypes.Select(x => $"{x.GetSlug()} - {x.GetFancyName()}").Aggregate((x, y) => x + "\n" + y)}```",
                        "Chat kinds unset", EmbedColorFine);

                    return;
                }

                if (args[0] == this.plugin.Config.DiscordBotPrefix + "setprefix" &&
                    await EnsureOwner(message.Author, message.Channel))
                {
                    // Are there parameters?
                    if (args.Length < 3)
                    {
                        await SendGenericEmbed(message.Channel,
                            $"You need to specify some chat kinds and a prefix to use.\nCheck the ``{this.plugin.Config.DiscordBotPrefix}help`` command for more information.",
                            "Error", EmbedColorError);

                        return;
                    }

                    var kinds = args[1].Split(',').Select(x => x.ToLower());

                    // Is there any chat type that's not recognized?
                    if (kinds.Any(x =>
                        XivChatTypeExtensions.TypeInfoDict.All(y => y.Value.Slug != x) && x != "any"))
                    {
                        await SendGenericEmbed(message.Channel,
                            $"One or more of the chat kinds you specified could not be found.\nCheck the ``{this.plugin.Config.DiscordBotPrefix}help`` command for more information.",
                            "Error", EmbedColorError);

                        return;
                    }

                    if (args[2] == "none")
                        args[2] = string.Empty;

                    foreach (var selectedKind in kinds)
                    {
                        // Special handling for chat types that share a type
                        if (selectedKind == "tell")
                        {
                            this.plugin.Config.PrefixConfigs[XivChatType.TellOutgoing] = args[2];
                            this.plugin.Config.PrefixConfigs[XivChatType.TellIncoming] = args[2];
                        }
                        else if (selectedKind == "p")
                        {
                            this.plugin.Config.PrefixConfigs[XivChatType.Party] = args[2];
                            this.plugin.Config.PrefixConfigs[XivChatType.CrossParty] = args[2];
                        }
                        else
                        {
                            var type = XivChatTypeExtensions.GetBySlug(selectedKind);
                            this.plugin.Config.PrefixConfigs[type] = args[2];
                        }

                    }

                    this.plugin.Config.Save();


                    await SendGenericEmbed(message.Channel,
                        $"OK! The following prefixes are set:\n\n```\n{this.plugin.Config.PrefixConfigs.Select(x => $"{x.Key.GetFancyName()} - {x.Value}").Aggregate((x, y) => x + "\n" + y)}```",
                        "Prefix set", EmbedColorFine);

                    return;
                }



                if (args[0] == this.plugin.Config.DiscordBotPrefix + "unsetprefix" &&
                    await EnsureOwner(message.Author, message.Channel))
                {
                    // Are there parameters?
                    if (args.Length < 2)
                    {
                        await SendGenericEmbed(message.Channel,
                            $"You need to specify some chat kinds and a prefix to use.\nCheck the ``{this.plugin.Config.DiscordBotPrefix}help`` command for more information.",
                            "Error", EmbedColorError);

                        return;
                    }

                    var kinds = args[1].Split(',').Select(x => x.ToLower());

                    // Is there any chat type that's not recognized?
                    if (kinds.Any(x =>
                        XivChatTypeExtensions.TypeInfoDict.All(y => y.Value.Slug != x) && x != "any"))
                    {
                        await SendGenericEmbed(message.Channel,
                            $"One or more of the chat kinds you specified could not be found.\nCheck the ``{this.plugin.Config.DiscordBotPrefix}help`` command for more information.",
                            "Error", EmbedColorError);

                        return;
                    }

                    foreach (var selectedKind in kinds)
                    {
                        var type = XivChatTypeExtensions.GetBySlug(selectedKind);
                        // Special handling for chat types that share a type
                        if (selectedKind == "tell")
                        {
                            this.plugin.Config.PrefixConfigs.Remove(XivChatType.TellOutgoing);
                            this.plugin.Config.PrefixConfigs.Remove(XivChatType.TellIncoming);
                        }
                        else if (selectedKind == "p")
                        {
                            this.plugin.Config.PrefixConfigs.Remove(XivChatType.Party);
                            this.plugin.Config.PrefixConfigs.Remove(XivChatType.CrossParty);
                        }
                        else
                        {
                            this.plugin.Config.PrefixConfigs.Remove(type);
                        }
                        
                    }

                    this.plugin.Config.Save();

                    if (this.plugin.Config.PrefixConfigs.Count == 0 )
                    {
                        await SendGenericEmbed(message.Channel,
                        $"All prefixes have been removed.",
                        "Prefix unset", EmbedColorFine);
                    }
                    else // this doesn't seem to trigger when there's only one entry left. I don't know why.
                    {
                        await SendGenericEmbed(message.Channel,
                        $"OK! The prefix for {XivChatTypeExtensions.GetBySlug(args[2])} has been removed.\n\n"
                        + $"The following prefixes are still set:\n\n```\n{this.plugin.Config.PrefixConfigs.Select(x => $"{x.Key.GetFancyName()} - {x.Value}").Aggregate((x, y) => x + "\n" + y)}```",
                        "Prefix unset", EmbedColorFine);
                    }

                    return;
                }

                if (args[0] == this.plugin.Config.DiscordBotPrefix + "setchattypename" &&
                    await EnsureOwner(message.Author, message.Channel))
                {
                    // Are there parameters?
                    if (args.Length < 3)
                    {
                        await SendGenericEmbed(message.Channel,
                            $"You need to specify one or more chat kinds and a custom name.\nCheck the ``{this.plugin.Config.DiscordBotPrefix}help`` command for more information.",
                            "Error", EmbedColorError);

                        return;
                    }

                    

                    var kinds = args[1].Split(',').Select(x => x.ToLower());
                    var chatChannelOverride = string.Join(" ", args.Skip(2)).Trim('"');

                    // Logger.Information($"arg1: {args[1]}; arg2: {chatChannelOverride}");

                    // Is there any chat type that's not recognized?
                    if (kinds.Any(x =>
                        XivChatTypeExtensions.TypeInfoDict.All(y => y.Value.Slug != x) && x != "any"))
                    {
                        await SendGenericEmbed(message.Channel,
                            $"One or more of the chat kinds you specified could not be found.\nCheck the ``{this.plugin.Config.DiscordBotPrefix}help`` command for more information.",
                            "Error", EmbedColorError);

                        return;
                    }

                    if (chatChannelOverride == "none")
                    {
                        foreach (var selectedKind in kinds)
                        {
                            var type = XivChatTypeExtensions.GetBySlug(selectedKind);
                            this.plugin.Config.CustomSlugsConfigs[type] = type.GetSlug();
                        }

                        await SendGenericEmbed(message.Channel,
                        $"OK! The following custom chat type names have been set:\n\n```\n{this.plugin.Config.CustomSlugsConfigs.Select(x => $"{x.Key.GetFancyName()} - {x.Value}").Aggregate((x, y) => x + "\n" + y)}```",
                        "Custom chat type set", EmbedColorFine);
                    }
                    else
                    {
                        foreach (var selectedKind in kinds)
                        {
                            // Special handling for chat types that share a type
                            if (selectedKind == "tell")
                            {
                                this.plugin.Config.CustomSlugsConfigs[XivChatType.TellOutgoing] = chatChannelOverride;
                                this.plugin.Config.CustomSlugsConfigs[XivChatType.TellIncoming] = chatChannelOverride;
                            }
                            else if (selectedKind == "p")
                            {
                                this.plugin.Config.CustomSlugsConfigs[XivChatType.Party] = chatChannelOverride;
                                this.plugin.Config.CustomSlugsConfigs[XivChatType.CrossParty] = chatChannelOverride;
                            }
                            else
                            {
                                var type = XivChatTypeExtensions.GetBySlug(selectedKind);
                                this.plugin.Config.CustomSlugsConfigs[type] = chatChannelOverride;
                            }
                        }

                        await SendGenericEmbed(message.Channel,
                        $"OK! The following custom chat type names have been set:\n\n```\n{this.plugin.Config.CustomSlugsConfigs.Select(x => $"{x.Key.GetFancyName()} - {x.Value}").Aggregate((x, y) => x + "\n" + y)}```",
                        "Custom chat type set", EmbedColorFine);
                    }

                    this.plugin.Config.Save();

                    return;
                }

                if (args[0] == this.plugin.Config.DiscordBotPrefix + "unsetchattypename" &&
                    await EnsureOwner(message.Author, message.Channel))
                {
                    // Are there parameters?
                    if (args.Length < 2)
                    {
                        await SendGenericEmbed(message.Channel,
                            $"One or more of the chat kinds you specified could not be found.\nCheck the ``{this.plugin.Config.DiscordBotPrefix}help`` command for more information.",
                            "Error", EmbedColorError);

                        return;
                    }



                    var kinds = args[1].Split(',').Select(x => x.ToLower());

                    Logger.Information($"Unsetting custom type name for arg1: {args[1]}");

                    // Is there any chat type that's not recognized?
                    if (kinds.Any(x =>
                        XivChatTypeExtensions.TypeInfoDict.All(y => y.Value.Slug != x) && x != "any"))
                    {
                        await SendGenericEmbed(message.Channel,
                            $"One or more of the chat kinds you specified could not be found.\nCheck the ``{this.plugin.Config.DiscordBotPrefix}help`` command for more information.",
                            "Error", EmbedColorError);

                        return;
                    }


                    foreach (var selectedKind in kinds)
                    {
                        var type = XivChatTypeExtensions.GetBySlug(selectedKind);
                        this.plugin.Config.CustomSlugsConfigs[type] = type.GetSlug();
                    }

                    await SendGenericEmbed(message.Channel,
                    $"OK! The following custom chat type names have been set:\n\n```\n{this.plugin.Config.CustomSlugsConfigs.Select(x => $"{x.Key.GetFancyName()} - {x.Value}").Aggregate((x, y) => x + "\n" + y)}```",
                    "Custom chat type unset", EmbedColorFine);


                    this.plugin.Config.Save();

                    return;
                }

                if (args[0] == this.plugin.Config.DiscordBotPrefix + "setduplicatems" &&
                    await EnsureOwner(message.Author, message.Channel))
                {
                    // Are there parameters?
                    if (args.Length != 2)
                    {
                        await SendGenericEmbed(message.Channel,
                            $"You need to specify a number in milliseconds to use.\nCheck the ``{this.plugin.Config.DiscordBotPrefix}help`` command for more information.",
                            "Error", EmbedColorError);

                        return;
                    }

                    // Make sure that it's a number (or assume it is)
                    if (!int.TryParse(args[1], out int newDelay))
                    {
                        await SendGenericEmbed(message.Channel,
                            $"You need to specify a positive number in milliseconds to use, or 0 to turn the feature off.\nCheck the ``{this.plugin.Config.DiscordBotPrefix}help`` command for more information.",
                            "Error", EmbedColorError);

                        return;
                    }


                    if (args[1].ToLower() == "none")
                        newDelay = 0;

                    if (newDelay < 0)
                    {
                        await SendGenericEmbed(message.Channel,
                            $"You need to specify a positive number in milliseconds to use, or 0 to turn the feature off.\nCheck the ``{this.plugin.Config.DiscordBotPrefix}help`` command for more information.",
                            "Error", EmbedColorError);

                        return;
                    }

                    this.plugin.Config.DuplicateCheckMS = newDelay;
                    this.plugin.Config.Save();

                    if (newDelay == 0)
                    {
                        await SendGenericEmbed(message.Channel,
                            $"OK! The duplicate chat removal feature has been disabled.", "Duplicate Message Check", EmbedColorFine);
                    }
                    else
                    {
                        await SendGenericEmbed(message.Channel,
                            $"OK! Any messages with the same content within the last **{newDelay}** milliseconds will be skipped, preventing duplicate posts.", "Duplicate Message Check", EmbedColorFine);
                    }

                    return;
                }

                if (args[0] == this.plugin.Config.DiscordBotPrefix + "toggledf" &&
                    await EnsureOwner(message.Author, message.Channel))
                {
                    if (!this.plugin.Config.ChannelConfigs.TryGetValue(message.Channel.Id, out var config))
                        config = new DiscordChannelConfig();

                    config.IsContentFinder = !config.IsContentFinder;

                    this.plugin.Config.ChannelConfigs[message.Channel.Id] = config;
                    this.plugin.Config.Save();

                    await SendGenericEmbed(message.Channel,
                        $"OK! This channel has been {(config.IsContentFinder ? "enabled" : "disabled")} from receiving Duty Finder notifications.",
                        "Duty Finder set", EmbedColorFine);

                    return;
                }

                if (args[0] == this.plugin.Config.DiscordBotPrefix + "toggleembed" &&
                    await EnsureOwner(message.Author, message.Channel))
                {
                    this.plugin.Config.ForceEmbedFallbackMode = !this.plugin.Config.ForceEmbedFallbackMode;
                    this.plugin.Config.Save();

                    await SendGenericEmbed(message.Channel,
                        $"OK! Sending relayed messages in embed fallback mode have been {(this.plugin.Config.ForceEmbedFallbackMode ? "**enabled**." : "**disabled**.\n\nPlease make sure Discord Chat Bridge is allowed to manage webhooks in your channels as needed.")}",
                        "Embed Fallback Mode set", EmbedColorFine);

                    return;
                }

                if (args[0] == this.plugin.Config.DiscordBotPrefix + "toggledefaultnameavatar" &&
                    await EnsureOwner(message.Author, message.Channel))
                {
                    this.plugin.Config.ForceDefaultNameAvatar = !this.plugin.Config.ForceDefaultNameAvatar;
                    this.plugin.Config.Save();

                    await SendGenericEmbed(message.Channel,
                        $"OK! Sending relayed messages in embed fallback mode have been {(this.plugin.Config.ForceDefaultNameAvatar ? "**enabled**.\n\nPlease make sure you have toggled sender names on, or you won't know who said which chat messages!" : "**disabled**.")}",
                        "Default Name and Avatar Mode set", EmbedColorFine);

                    if (this.plugin.Config.ForceDefaultNameAvatar && this.socketClient.CurrentUser.Username.ToLower().Contains("discord"))
                    {
                        await SendGenericEmbed(message.Channel, "Bot username cannot contain Discord. Using a fallback value until this is changed.", "ERROR - Bot username cannot contain Discord", EmbedColorError);
                    }

                    return;
                }
                

                if (args[0] == this.plugin.Config.DiscordBotPrefix + "togglesender" &&
                    await EnsureOwner(message.Author, message.Channel))
                {
                    this.plugin.Config.SenderInMessage = !this.plugin.Config.SenderInMessage;
                    this.plugin.Config.Save();

                    await SendGenericEmbed(message.Channel,
                        $"OK! Discord Chat Bridge **{(this.plugin.Config.SenderInMessage ? "will" : "will not")}** include the sender name in all messages (where possible).",
                        "Sender In Message Mode set", EmbedColorFine);

                    return;
                }

                if (args[0] == this.plugin.Config.DiscordBotPrefix + "setcfprefix" &&
                    await EnsureOwner(message.Author, message.Channel))
                {
                    // Are there parameters?
                    if (args.Length < 2)
                    {
                        await SendGenericEmbed(message.Channel,
                            $"You need to specify a prefix to use, or type \"none\" if you want to remove it.\nCheck the ``{this.plugin.Config.DiscordBotPrefix}help`` command for more information.",
                            "Error", EmbedColorError);

                        return;
                    }

                    if (args[1] == "none")
                        args[1] = string.Empty;

                    this.plugin.Config.CFPrefixConfig = args[1];

                    this.plugin.Config.Save();


                    await SendGenericEmbed(message.Channel,
                        $"OK! The following prefix was set:\n\n```\n{this.plugin.Config.CFPrefixConfig}```",
                        "Prefix set", EmbedColorFine);

                    return;
                }

                if (args[0] == this.plugin.Config.DiscordBotPrefix + "listchannel" &&
                    await EnsureOwner(message.Author, message.Channel))
                {
                    if (!this.plugin.Config.ChannelConfigs.TryGetValue(message.Channel.Id, out var config))
                    {
                        await SendGenericEmbed(message.Channel,
                            $"You didn't set up any channel kinds for this channel yet.\nPlease use the ``{this.plugin.Config.DiscordBotPrefix}setchannel`` command to do this.",
                            "Error", EmbedColorError);
                        return;
                    }

                    if (config == null || config.ChatTypes.Count == 0) 
                    {
                        await SendGenericEmbed(message.Channel,
                            $"There are no channel kinds set for this channel right now.\nPlease use the ``{this.plugin.Config.DiscordBotPrefix}setchannel`` command to do this.",
                            "Error", EmbedColorFine);
                        return;
                    }

                    await SendGenericEmbed(message.Channel,
                        $"OK! This channel has been set to receive the following chat kinds:\n\n```\n{config.ChatTypes.Select(x => $"{x.GetFancyName()}").Aggregate((x, y) => x + "\n" + y)}```",
                        "Chat kinds set", EmbedColorFine);

                    return;
                }

                if (args[0] == this.plugin.Config.DiscordBotPrefix + "setavatar")
                {
                    // Are there parameters?
                    if (args.Length != 3)
                    {
                        await SendGenericEmbed(message.Channel,
                            $"You need to specify one or more chat kinds and a custom avatar url.\nCheck the ``{this.plugin.Config.DiscordBotPrefix}help`` command for more information.",
                            "Error", EmbedColorError);

                        return;
                    }

                    var kinds = args[1].Split(',').Select(x => x.ToLower());
                    var avatarURL = args[2].Replace("<", "").Replace(">", "").Trim();


                    if (args[1] == "default" && avatarURL == "none")
                    {
                        this.plugin.Config.DefaultAvatarURL = Constant.LogoLink;

                        await SendGenericEmbed(message.Channel,
                        $"OK! The default/fallback avatar has been reset to default.",
                        "Custom fallback avatar reset", EmbedColorFine);

                        this.plugin.Config.Save();
                        return;
                    }
                    if (args[1] == "default")
                    {
                        this.plugin.Config.DefaultAvatarURL = avatarURL;

                        await SendGenericEmbed(message.Channel,
                        $"OK! The default/fallback avatar has been set to ``{avatarURL}``",
                        "Custom fallback avatar set", EmbedColorFine);

                        this.plugin.Config.Save();
                        return;
                    }
                    

                    // Is there any chat type that's not recognized?
                    if (kinds.Any(x =>
                        XivChatTypeExtensions.TypeInfoDict.All(y => y.Value.Slug != x) && x != "any"))
                    {
                        await SendGenericEmbed(message.Channel,
                            $"One or more of the chat kinds you specified could not be found.\nCheck the ``{this.plugin.Config.DiscordBotPrefix}help`` command for more information.",
                            "Error", EmbedColorError);

                        return;
                    }

                    if (avatarURL == "none")
                    {
                        foreach (var selectedKind in kinds)
                        {
                            var type = XivChatTypeExtensions.GetBySlug(selectedKind);
                            this.plugin.Config.ChatTypeAvatarURL[type] = Constant.LogoLink;
                        }

                        await SendGenericEmbed(message.Channel,
                        $"OK! The following custom chat type names have been set:\n\n```\n{this.plugin.Config.CustomSlugsConfigs.Select(x => $"{x.Key.GetFancyName()} - {x.Value}").Aggregate((x, y) => x + "\n" + y)}```",
                        "Custom avatar set", EmbedColorFine);
                    }
                    else
                    {
                        foreach (var selectedKind in kinds)
                        {
                            // Special handling for chat types that share a type
                            if (selectedKind == "tell")
                            {
                                this.plugin.Config.ChatTypeAvatarURL[XivChatType.TellOutgoing] = avatarURL;
                                this.plugin.Config.ChatTypeAvatarURL[XivChatType.TellIncoming] = avatarURL;
                            }
                            else if (selectedKind == "p")
                            {
                                this.plugin.Config.ChatTypeAvatarURL[XivChatType.Party] = avatarURL;
                                this.plugin.Config.ChatTypeAvatarURL[XivChatType.CrossParty] = avatarURL;
                            }
                            else
                            {
                                var type = XivChatTypeExtensions.GetBySlug(selectedKind);
                                this.plugin.Config.ChatTypeAvatarURL[type] = avatarURL;
                            }
                        }

                        await SendGenericEmbed(message.Channel,
                        $"OK! The following custom chat type names have been set:\n\n```\n{this.plugin.Config.ChatTypeAvatarURL.Select(x => $"{x.Key.GetFancyName()} - {x.Value}").Aggregate((x, y) => x + "\n" + y)}```",
                        "Custom chat type set", EmbedColorFine);
                    }

                    this.plugin.Config.Save();

                    return;
                }

                if (args[0] == this.plugin.Config.DiscordBotPrefix + "unsetavatar")
                {
                    if (args.Length != 2)
                    {
                        await SendGenericEmbed(message.Channel,
                            $"You have entered this command incorrectly. Please try again.",
                            "Error", EmbedColorError);
                    }

                    if (args[1] == "default")
                    {
                        this.plugin.Config.DefaultAvatarURL = Constant.LogoLink;

                        await SendGenericEmbed(message.Channel,
                        $"OK! The default/fallback avatar has been reset.",
                        "Custom fallback avatar reset", EmbedColorFine);

                        this.plugin.Config.Save();
                        return;
                    }

                    var kinds = args[1].Split(',').Select(x => x.ToLower());

                    // Logger.Information($"Looking for `{chatTypeSlug}`");

                    // Is there any chat type that's not recognized?
                    if (kinds.Any(x =>
                        XivChatTypeExtensions.TypeInfoDict.All(y => y.Value.Slug != x) && x != "any"))
                    {
                        await SendGenericEmbed(message.Channel,
                            $"One or more of the chat kinds you specified could not be found.\nCheck the ``{this.plugin.Config.DiscordBotPrefix}help`` command for more information.",
                            "Error", EmbedColorError);

                        return;
                    }

                    foreach (var selectedKind in kinds)
                    {
                        // Special handling for chat types that share a type
                        if (selectedKind == "tell")
                        {
                            this.plugin.Config.ChatTypeAvatarURL.Remove(XivChatType.TellOutgoing);
                            this.plugin.Config.ChatTypeAvatarURL.Remove(XivChatType.TellIncoming);
                        }
                        else if (selectedKind == "p")
                        {
                            this.plugin.Config.ChatTypeAvatarURL.Remove(XivChatType.Party);
                            this.plugin.Config.ChatTypeAvatarURL.Remove(XivChatType.CrossParty);
                        }
                        else
                        {
                            var type = XivChatTypeExtensions.GetBySlug(selectedKind);
                            this.plugin.Config.ChatTypeAvatarURL.Remove(type);
                        }
                    }

                    if (this.plugin.Config.ChatTypeAvatarURL.Count == 0)
                    {
                        await SendGenericEmbed(message.Channel,
                            $"OK! There are no custom avatar overrides set.",
                            "Custom chat type unset", EmbedColorFine);
                    }
                    else
                    {
                        await SendGenericEmbed(message.Channel,
                            $"OK! The following custom chat type names are set:\n\n```\n{this.plugin.Config.ChatTypeAvatarURL.Select(x => $"{x.Key.GetFancyName()} - {x.Value}").Aggregate((x, y) => x + "\n" + y)}```",
                            "Custom chat type unset", EmbedColorFine);
                    }
                    

                    plugin.Config.Save();
                    return;
                    
                }

                if (args[0] == this.plugin.Config.DiscordBotPrefix + "help")
                {
                    Logger.Verbose("Help time");

                    var builder = new EmbedBuilder()
                        .WithTitle("Discord Bridge Help")
                        .WithDescription("你可以使用以下命令来配置Discord bridge。")
                        .WithColor(new Color(EmbedColorFine))
                        .AddField($"{this.plugin.Config.DiscordBotPrefix}setchannel", "选择哪些聊天类型的消息会被发送至当前Discord频道。\n" +
                                                 $"格式: ``{this.plugin.Config.DiscordBotPrefix}setchannel <kind1,kind2,...>``\n\n" +
                                                 $"[点击查看全部聊天类型]({Constant.KindListLink}) 或输入 ``any`` 将会发送全部聊天类型的内容。")
                        //$"The following chat kinds are available:\n```all - All regular chat\n{XivChatTypeExtensions.TypeInfoDict.Select(x => $"{x.Value.Slug} - {x.Value.FancyName}").Aggregate((x, y) => x + "\n" + y)}```")
                        .AddField($"{this.plugin.Config.DiscordBotPrefix}unsetchannel", "就像上一个命令, 但是移除指定的聊天类型。")
                        .AddField($"{this.plugin.Config.DiscordBotPrefix}listchannel", "显示会发送到当前Discord频道的聊天类型列表。")
                        .AddField($"{this.plugin.Config.DiscordBotPrefix}send", "发送消息到游戏内。\n**警告**:它操作起来和游戏内类似，你极有可能发送到你不想发送的消息类型，而且你很难直观的看到自己当前所处的类型。\n为了防止这个问题，你可以使用`setsendkind`来配置一个默认消息发送类型。\n"
                        + $"使用宏时，这里遵循和游戏完全一致的交互逻辑，比如你可以`send /tell aaa@柔风海湾`，然后使用`send 测试消息`来发送私聊给`aaa@柔风海湾`。\n"
                        + $"**注意**，以上宏的使用和`setsendkind`默认消息发送类型的设置相互冲突，默认消息类型会覆盖使用宏指定的频道。")
                        .AddField($"{this.plugin.Config.DiscordBotPrefix}setsendkind", "为当前Discord频道设置一个默认的消息发送类型。"
                            + $"这将会为你在Discord发到游戏中的每一条消息加上消息类型宏，例如，当你设置默认发送类型为p时，使用`send 测试消息`时，会自动转换为`send /p 测试消息`。\n"
                            + $"如果你不想向默认类型发送消息，你仍然可以直接使用例如`send /p 测试消息`来发送消息。\n"
                            + $"如果你想使用私聊作为类型，可以使用`setsendkind tell aa@柔风海湾`。\n"
                            + $"你也可以设置为 `none` 如果你想要移除这个设置。\n"
                            + $"格式: ``{this.plugin.Config.DiscordBotPrefix}setsendkind <kind>``")
                        .AddField($"{this.plugin.Config.DiscordBotPrefix}toggledefaultnameavatar", "开启或关闭使用你配置的机器人名字和默认的机器人头像来发送webhook消息。\n**警告:**这应该与`togglesender`功能相结合，否则你无法区分不同玩家的消息。")
                        .AddField($"{this.plugin.Config.DiscordBotPrefix}toggledf", "开启或关闭发送任务搜索器的情况到当前Discord频道。")
                        .AddField($"{this.plugin.Config.DiscordBotPrefix}toggleembed", "开启或关闭使用Webhooks (默认)或Embeds (备选方案)方式发送消息。")
                        .AddField($"{this.plugin.Config.DiscordBotPrefix}togglesender", "开启或关闭向Discord频道发送聊天内容时在消息中包含发送者名称。")
                        .AddField($"{this.plugin.Config.DiscordBotPrefix}setduplicatems", "设置机器人检查过去的消息是否相同的时间（以毫秒为单位）。默认值为 0 毫秒。")
                        .AddField($"{this.plugin.Config.DiscordBotPrefix}setprefix", "设置聊天类型的前缀。 "
                            + $"可以是一个表情符号或一个字符串，它将被添加到对应聊天类型收到的每个聊天消息的前面。 "
                            + $"你也可以设置为 `none` 如果你想要移除它。\n" 
                            + $"格式: ``{this.plugin.Config.DiscordBotPrefix}setprefix <kind1,kind2,...> <prefix>``")
                        .AddField($"{this.plugin.Config.DiscordBotPrefix}setcfprefix", "设置任务搜索器消息的前缀。"
                            + $"你也可以设置为 `none` 如果你想要移除它。\n" 
                            + $"格式: ``{this.plugin.Config.DiscordBotPrefix}setcfprefix <prefix>``")
                        .AddField($"{this.plugin.Config.DiscordBotPrefix}setchattypename ", "为聊天类型设置自定义文本。"
                            + $"可以是一个表情符号或一个字符串，它将替换此聊天类型收到的每个聊天消息的聊天类型的缩写。 "
                            + $"你也可以设置为 `none` 如果你想要移除它。\n" 
                            + $"格式: ``{this.plugin.Config.DiscordBotPrefix}setchattypename  <kind1,kind2,...> <custom text>``")
                        .AddField($"{this.plugin.Config.DiscordBotPrefix}unsetprefix", "为指定聊天类型移除前缀设置。 \n"
                            + $"格式: ``{this.plugin.Config.DiscordBotPrefix}unsetprefix <kind>``")
                        .AddField($"{this.plugin.Config.DiscordBotPrefix}unsetchattypename", "为指定聊天类型移除自定义文本设置。 \n"
                            + $"格式: ``{this.plugin.Config.DiscordBotPrefix}unsetchattypename <kind>``")
                        .AddField($"{this.plugin.Config.DiscordBotPrefix}setavatar <kind> <url>", "为指定聊天类型设置备用头像。 "
                            + "对于未配置的覆写，将会使用 ``default`` 作为备用设置。\n"
                            + "__注意__: 如果你还没有URL，请先将图标上传到Discord。\n"
                            + $"格式: ``{this.plugin.Config.DiscordBotPrefix}setavatar <kind> <url>``")
                        .AddField($"{this.plugin.Config.DiscordBotPrefix}unsetavatar <kind>", "为指定聊天类型移除备用头像设置。 "
                            + "对于未配置的覆写，将会使用 ``default`` 来重置备用设置。\n"
                            + $"格式: ``{this.plugin.Config.DiscordBotPrefix}unsetavatar <kind>``")
                        .AddField("需要更多帮助?",
                            $"你可以 [阅读这篇详细教程]({Constant.HelpLink}) 或 [加入我们的Discord Server]({Constant.DiscordJoinLink}) 来寻求帮助。")
                        .WithFooter(footer =>
                        {
                            footer
                                .WithText("Dalamud Chat Bridge")
                                .WithIconUrl(Constant.LogoLink);
                        })
                        .WithThumbnailUrl(Constant.LogoLink);
                    var embed = builder.Build();

                    var m = await message.Channel.SendMessageAsync(
                            null,
                            embed: embed)
                        .ConfigureAwait(false);
                    ;
                    Logger.Verbose(m.Id.ToString());

                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Could not handle incoming Discord message.");
            }
        }

        private static async Task SendGenericEmbed(ISocketMessageChannel channel, string message, string title, uint color)
        {
            var builder = new EmbedBuilder()
                .WithTitle(title)
                .WithDescription(message)
                .WithColor(new Color(color))
                .WithFooter(footer => {
                    footer
                        .WithText("Dalamud Chat Bridge")
                        .WithIconUrl(Constant.LogoLink);
                })
                .WithThumbnailUrl(Constant.LogoLink);
                
            var embed = builder.Build();
            await channel.SendMessageAsync(
                    null,
                    embed: embed)
                .ConfigureAwait(false);
        }

        private static async Task SendPrettyEmbed(ISocketMessageChannel channel, string message, string title, string iconurl, uint color)
        {
            var builder = new EmbedBuilder()
                .WithTitle(title)
                .WithDescription(message)
                .WithColor(new Color(color))
                .WithFooter(footer => {
                    footer
                        .WithText("Dalamud Chat Bridge")
                        .WithIconUrl(Constant.LogoLink);
                })
                .WithThumbnailUrl(iconurl);

            var embed = builder.Build();
            await channel.SendMessageAsync(
                    null,
                    embed: embed)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Check if the sender of this message is set as the owner of this plugin, and send an error message to the specified channel if not null.
        /// </summary>
        /// <param name="user">User in question.</param>
        /// <param name="errorMessageChannel">Channel for error message.</param>
        /// <returns>True if the user is the owner of this plugin.</returns>
        private async Task<bool> EnsureOwner(IUser user, ISocketMessageChannel errorMessageChannel = null)
        {
            Logger.Verbose("EnsureOwner: " + user.Username + "#" + user.Discriminator);
            if (user.Username + "#" + user.Discriminator == this.plugin.Config.DiscordOwnerName) 
                return true;

            // bandaid for Discord's username changes.
            if (user.Username == this.plugin.Config.DiscordOwnerName && user.Discriminator == "0000")
                return true;

            if (ulong.TryParse(this.plugin.Config.DiscordOwnerName, out ulong parsed))
                if (user.Id == parsed)
                    return true;

            if (errorMessageChannel == null) 
                return false;

            await SendGenericEmbed(errorMessageChannel, "You are not allowed to run commands for this bot.\n\nIf this is your bot, please use the \"/pdiscord\" command in-game to enter your username or Discord User ID number.", "Error", EmbedColorError);

            return false;
        }

        public async Task SendItemSaleEvent(SeString name, string iconurl, uint itemId, string message, XivChatType chatType)
        {
            var applicableChannels =
                this.plugin.Config.ChannelConfigs.Where(x => x.Value.ChatTypes.Contains(chatType));

            if (!applicableChannels.Any())
                return;

            message = this.specialChars.TransformToUnicode(message);

            
            Logger.Information($"Retainer sold itemID: {itemId} with iconurl: {iconurl}");

            this.plugin.Config.PrefixConfigs.TryGetValue(chatType, out var prefix);

            foreach (var channelConfig in applicableChannels)
            {
                var socketChannel = this.socketClient.GetChannel(channelConfig.Key);

                if (socketChannel == null)
                {
                    Logger.Error("Could not find channel {0} for {1}", channelConfig.Key, chatType);
                    continue;
                }

                // add handling for webhook vs embed here
                IGuildChannel guildChannel = (IGuildChannel)socketChannel;
                IGuildUser guildUser = await guildChannel.Guild.GetUserAsync(this.socketClient.CurrentUser.Id);
                bool hasManageWebHooks = guildUser.GetPermissions(guildChannel).Has(ChannelPermission.ManageWebhooks);

                if (socketChannel is SocketDMChannel)
                {
                    var DMChannel = await this.socketClient.GetDMChannelAsync(channelConfig.Key);
                    await SendPrettyEmbed((ISocketMessageChannel)DMChannel, message, $"Retainer sold {name}", iconurl, EmbedColorFine);
                }
                else if (!hasManageWebHooks)
                {
                    Logger.Debug("FALLBACKMODE - Unable to create WebHook - No permission\n");
                    await SendPrettyEmbed((ISocketMessageChannel)socketChannel, $"FALLBACKMODE\n\nMissing ManageWebHooks permission.\n\n{message}", $"Retainer sold {name}", iconurl, EmbedColorError);
                }
                else
                {
                    var webhookClient = await GetOrCreateWebhookClient(socketChannel);
                    if (webhookClient != null)
                    {
                        await webhookClient.SendMessageAsync($"{prefix} {message}",
                        username: $"Retainer sold {name}", avatarUrl: iconurl);
                    }
                    else
                    {
                        Logger.Debug("FALLBACKMODE - Unable to create WebHook\n");
                        await SendPrettyEmbed((ISocketMessageChannel)socketChannel, $"FALLBACKMODE\nUnable to create WebHook\n\n{message}", $"Retainer sold {name}", iconurl, EmbedColorError);
                    }
                    
                }
                    
            }

        }

        public async Task SendChatEvent(string message, string senderName, string senderWorld, XivChatType chatType, string avatarUrl = "")
        {
            // set fields for true chat messages or custom via ipc
            if (chatType != XivChatTypeExtensions.IpcChatType)
            {
                // Special case for outgoing tells, these should be sent under Incoming tells
                if (chatType == XivChatType.TellOutgoing) {
                    chatType = XivChatType.TellIncoming;
                }
            }
            else
            {
                senderWorld = null;
            }

            // default avatar url to logo link if empty
            if (string.IsNullOrEmpty(avatarUrl))
            {
                
                if (!plugin.Config.ChatTypeAvatarURL.TryGetValue(chatType, out avatarUrl))
                {
                    avatarUrl = plugin.Config.DefaultAvatarURL;
                }
                
            }

            var applicableChannels =
                this.plugin.Config.ChannelConfigs.Where(x => x.Value.ChatTypes.Contains(chatType));

            if (!applicableChannels.Any())
                return;

            message = this.specialChars.TransformToUnicode(message);

            bool characterSearchFailed = false;
            
            try
            {
                switch (chatType)
                {
                    case XivChatType.Echo:
                        break;
                    case (XivChatType)61: // npc talk
                        break;
                    case (XivChatType)68: // npc announce
                        break;
                    default:
                        // don't even bother searching if it's gonna be invalid
                        bool doSearch = true;
                        
                        if (string.IsNullOrEmpty(senderName))
                        {
                            Logger.Debug($"Sender Name was null or empty");
                            senderName = $"FFXIV Bridge Worker {plugin.cachedLocalPlayer?.Name ?? "Unknown LocalPlayer"}";
                            senderWorld = "";
                            doSearch = false;
                        }
                        if (string.IsNullOrEmpty(senderWorld))
                        {
                            Logger.Debug($"Sender World was null or empty: {senderWorld}");
                            doSearch = false;
                        }
                        
                        // special cases for things that aren't coming from FFXIV directly.
                        if (senderName == "Sonar")
                        {
                            Logger.Debug($"Sender Name was {senderName}");
                            doSearch = false;
                        }
                        //else if (!senderName.Contains(' '))
                        //{
                        //    Logger.Debug($"Sender Name invalid: {senderName}");
                        //    doSearch = false;
                        //}


                        if (doSearch)
                        {
                            var playerCacheName = $"{senderName}＠{senderWorld}";
                            Logger.Debug($"Searching for {playerCacheName}");
                            
                            if (CachedResponses.TryGetValue(playerCacheName, out LodestoneCNPlayer lschar))
                            {
                                Logger.Debug($"Retrived cached data for {lschar.Character_Name} {lschar.Avatar}");
                                if (!string.IsNullOrEmpty(lschar.Avatar))
                                {
                                    avatarUrl = lschar.Avatar;
                                }
                            }
                            else
                            {
                                Logger.Debug($"Searching lodestone for {playerCacheName}");
                                
                                lschar = await lodestoneClient.SearchPlayer(
                                    senderName,
                                    senderWorld
                                );
                                if (lschar == null)
                                {
                                    Logger.Debug($"No data found for {playerCacheName}");
                                    break;
                                }

                                CachedResponses.TryAdd(playerCacheName, lschar);
                                Logger.Debug($"Adding cached data for {lschar.Character_Name} {lschar.Avatar}");
                                if (!string.IsNullOrEmpty(lschar.Avatar))
                                {
                                    avatarUrl = lschar.Avatar;
                                }
                            }

                            // avatarUrl = (await XivApiClient.GetCharacterSearch(senderName, senderWorld)).AvatarUrl;
                        }
                        
                        break;
                }                    
            }
            catch (Exception ex)
            {
                if (string.IsNullOrEmpty(senderName))
                {
                    Logger.Error($"senderName was null or empty. How did we get this far?");
                    senderName = "Bridge Error - sendername";
                }
                else
                {
                    Logger.Error(ex, $"Cannot fetch XIVAPI character search for {senderName} on {senderWorld}");
                }
                
                characterSearchFailed = true;
            }

            var displayName = senderName + (string.IsNullOrEmpty(senderWorld) || string.IsNullOrEmpty(senderName)
                ? ""
                : $"＠{senderWorld}");

            this.plugin.Config.PrefixConfigs.TryGetValue(chatType, out var prefix);

            bool senderInMessage = this.plugin.Config.SenderInMessage;

            var chatTypeText = this.plugin.Config.CustomSlugsConfigs.TryGetValue(chatType, out var x) ? x : chatType.GetSlug();
            

            foreach (var channelConfig in applicableChannels)
            {
                var socketChannel = this.socketClient.GetChannel(channelConfig.Key);

                if (socketChannel == null)
                {
                    Logger.Error("Could not find channel {0} for {1}", channelConfig.Key, chatType);

                    // try one more time just in case.
                    socketChannel = this.socketClient.GetChannel(channelConfig.Key);

                    if (socketChannel is null && !characterSearchFailed)
                    {
                        var channelConfigs = this.plugin.Config.ChannelConfigs;
                        channelConfigs.Remove(channelConfig.Key);
                        this.plugin.Config.ChannelConfigs = channelConfigs;
                        
                        Logger.Info("Removing channel {0}'s config because it no longer exists or cannot be accessed.", channelConfig.Key);
                        this.plugin.Config.Save();
                    }
                    
                    continue;
                }

                var messageContent = senderInMessage ? $"{displayName}: {message}" : message;

                messageContent = chatType != XivChatTypeExtensions.IpcChatType ? $"{prefix}**[{chatTypeText}]** {messageContent}" : $"{prefix} {messageContent}";


                // add handling for webhook vs embed here
                IGuildChannel guildChannel = (IGuildChannel)socketChannel;
                IGuildUser guildUser = await guildChannel.Guild.GetUserAsync(this.socketClient.CurrentUser.Id);
                bool hasManageWebHooks = guildUser.GetPermissions(guildChannel).Has(ChannelPermission.ManageWebhooks);

                if (socketChannel is SocketDMChannel)
                {
                    var DMChannel = await this.socketClient.GetDMChannelAsync(channelConfig.Key);
                    await SendPrettyEmbed((ISocketMessageChannel)DMChannel, messageContent, displayName, avatarUrl, EmbedColorFine);
                    Logger.Debug("SendChatEvent sent to DMs.");
                }
                else if (this.plugin.Config.ForceEmbedFallbackMode)
                {
                    await SendPrettyEmbed((ISocketMessageChannel)socketChannel, $"{messageContent}", $"{displayName}", avatarUrl, EmbedColorFine);
                }
                else if (!hasManageWebHooks)
                {
                    Logger.Debug("FALLBACKMODE - Unable to create WebHook - No Permission\n");
                    await SendPrettyEmbed((ISocketMessageChannel)socketChannel, $"FALLBACKMODE\n\nMissing ManageWebHooks permission.\n\n{messageContent}", $"{displayName}", avatarUrl, EmbedColorError);
                }
                else
                {
                    var webhookClient = await GetOrCreateWebhookClient(socketChannel);

                    if (duplicateFilter.CheckAlreadySent(socketChannel, slug: chatTypeText, displayName, chatText: message))
                    {
                        continue;
                    }

                    if (webhookClient != null)
                    {
                        if (this.plugin.Config.ForceDefaultNameAvatar)
                        {
                            displayName = this.socketClient.CurrentUser.Username.ToLower().Contains("discord") ? "Dalamud Chat Bridge" : this.socketClient.CurrentUser.Username;
                            avatarUrl = plugin.Config.DefaultAvatarURL;
                        }
                        
                        await webhookClient.SendMessageAsync(
                            messageContent, username: displayName, avatarUrl: avatarUrl,
                            allowedMentions: new AllowedMentions(AllowedMentionTypes.Roles | AllowedMentionTypes.Users | AllowedMentionTypes.None)
                        );
                        Logger.Debug("SendChatEvent sent to WebHook.");
                    }
                    else
                    {
                        Logger.Debug("FALLBACKMODE - Unable to create WebHook - Unknown failure\n");
                        await SendPrettyEmbed((ISocketMessageChannel)socketChannel, $"FALLBACKMODE\n\nUnable to create WebHook\n\n{messageContent}", $"{displayName}", avatarUrl, EmbedColorError);
                    }
                }
            }
        }

        public async Task SendContentFinderEvent(QueuedContentFinderEvent cfEvent)
        {
            var applicableChannels =
                this.plugin.Config.ChannelConfigs.Where(x => x.Value.IsContentFinder);

            if (!applicableChannels.Any())
                return;

            var iconFolder = cfEvent.ContentFinderCondition.Image / 1000 * 1000;

            var embedBuilder = new EmbedBuilder()
                .WithCurrentTimestamp()
                .WithColor(0x297c00)
                .WithTitle("Duty is ready: " + cfEvent.ContentFinderCondition.Name)
                .WithImageUrl("https://xivapi.com" + $"/i/{iconFolder}/{cfEvent.ContentFinderCondition.Image}.png")
                .WithFooter(footer =>
                {
                    footer
                        .WithText("For: " + plugin.cachedLocalPlayer?.Name ?? "Unknown LocalPlayer")
                        .WithIconUrl(Constant.LogoLink);
                });

            foreach (var channelConfig in applicableChannels)
            {
                var socketChannel = this.socketClient.GetChannel(channelConfig.Key);

                if (socketChannel == null)
                {
                    Logger.Error("Could not find channel {0} for cfc", channelConfig.Key);
                    continue;
                }

                var prefix = this.plugin.Config.CFPrefixConfig ?? "";

                // add handling for webhook vs embed here
                IGuildChannel guildChannel = (IGuildChannel)socketChannel;
                IGuildUser guildUser = await guildChannel.Guild.GetUserAsync(this.socketClient.CurrentUser.Id);
                bool hasManageWebHooks = guildUser.GetPermissions(guildChannel).Has(ChannelPermission.ManageWebhooks);

                if (socketChannel is SocketDMChannel)
                {
                    embedBuilder.WithAuthor(new EmbedAuthorBuilder {Name = "Dalamud Chat Bridge", IconUrl = Constant.LogoLink});
                    var DMChannel = await this.socketClient.GetDMChannelAsync(channelConfig.Key);
                    await DMChannel.SendMessageAsync($"{prefix}", embed: embedBuilder.Build());
                }
                else if (!hasManageWebHooks)
                {
                    Logger.Debug("FALLBACKMODE - Unable to create WebHook - No Permission\n");
                    embedBuilder
                        .WithAuthor(new EmbedAuthorBuilder { Name = "Dalamud Chat Bridge", IconUrl = Constant.LogoLink })
                        .WithDescription("FALLBACKMODE - Unable to create WebHook - Missing ManageWebHook permission.");
                    await ((ISocketMessageChannel)socketChannel).SendMessageAsync($"{prefix}", embed: embedBuilder.Build());
                }
                else
                {
                    var webhookClient = await GetOrCreateWebhookClient(socketChannel);
                    

                    if (webhookClient != null)
                    {
                        await webhookClient.SendMessageAsync($"{prefix}", embeds: [embedBuilder.Build()],
                    username: "Dalamud Chat Bridge", avatarUrl: Constant.LogoLink);
                    }
                    else
                    {
                        Logger.Debug("FALLBACKMODE - Unable to create WebHook - Unknown error\n");
                        embedBuilder
                            .WithAuthor(new EmbedAuthorBuilder { Name = "Dalamud Chat Bridge", IconUrl = Constant.LogoLink })
                            .WithDescription("FALLBACKMODE - Unable to create WebHook - Unknown failure");
                        await ((ISocketMessageChannel)socketChannel).SendMessageAsync($"{prefix}", embed: embedBuilder.Build());
                    }
                }

                
            }
        }

        /// <summary>
        /// Get the webhook for the respective channel, or create one if it doesn't exist.
        /// </summary>
        /// <param name="channel">The channel to get the webhook for</param>
        /// <returns><see cref="IWebhook"/> for the respective channel.</returns>
        private async Task<DiscordWebhookClient> GetOrCreateWebhookClient(SocketChannel channel)
        {
            if (channel is SocketTextChannel textChannel)
            {
                if (!this.plugin.Config.ChannelConfigs.TryGetValue(channel.Id, out var channelConfig))
                    throw new ArgumentException("No configuration for channel.", nameof(channel));

                IWebhook hook;
                if (channelConfig.WebhookId != 0)
                    hook = await textChannel.GetWebhookAsync(channelConfig.WebhookId) ?? await textChannel.CreateWebhookAsync("FFXIV Bridge Worker");
                else
                {
                    try
                    {
                        hook = await textChannel.CreateWebhookAsync("FFXIV Bridge Worker");
                    }
                    catch (Discord.Net.HttpException e)
                    {
                        Logger.Error("Unable to get or create webhook", e.StackTrace);
                        return null;
                    }
                }


                this.plugin.Config.ChannelConfigs[channel.Id].WebhookId = hook.Id;
                this.plugin.Config.Save();

                Logger.Verbose("Webhook for {0} OK!! {1}", channel.Id, hook.Id);

                return new DiscordWebhookClient(hook.Id, hook.Token, new DiscordRestConfig
                {
                    RestClientProvider = DiscordRsetClientProvider.Instance
                });
            }

            throw new ArgumentNullException(nameof(channel));
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                Logger.Verbose("Discord DISPOSE!!");
                this.MessageQueue?.Stop();
                this.socketClient?.LogoutAsync().GetAwaiter().GetResult();
                this.socketClient?.Dispose();
                this.lodestoneClient.Dispose();
            }
        }
    }
}
