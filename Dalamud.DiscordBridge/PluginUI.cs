using System.Diagnostics;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using ImGuiNET;

namespace Dalamud.DiscordBridge
{
    public class PluginUI(DiscordBridgePlugin plugin)
    {
        static readonly IPluginLog Logger = Service.Logger;
        private readonly DiscordBridgePlugin Plugin = plugin;
        private bool isVisible;

        private string token;
        private string username;
        private bool disableSave = false;
        private bool useProxy = true;
        private bool useSystemProxy = true;
        private string proxyAddress = "";
        private bool changedProxy = false;

        private static Vector4 errorColor = new(1f, 0f, 0f, 1f);
        private static Vector4 fineColor = new(0.337f, 1f, 0.019f, 1f);

        public void Show()
        {
            this.token = this.Plugin.Config.DiscordToken;
            this.username = this.Plugin.Config.DiscordOwnerName;
            this.useProxy = this.Plugin.Config.UseProxy;
            this.useSystemProxy = this.Plugin.Config.UseSystemProxy;
            this.proxyAddress = this.Plugin.Config.ProxyAddress;

            this.isVisible = true;
        }

        public void Draw()
        {
            if (!this.isVisible)
                return;

            ImGui.Begin("Discord Bridge Setup", ref this.isVisible);

            ImGui.Text("在这个窗口，你可以配置XIVLauncher Discord Bridge。\n\n" +
                       "要开始，在下方输入你的Discord机器人token和你的用户名或用户ID，然后点击 \"保存\"。\n" +
                       "当出现绿色的 \"连接成功\"时, 点击 \"添加到我的Server\" 按钮来添加机器人到你的个人Server中。\n" +
                       $"你可以在你的服务器中使用 {this.Plugin.Config.DiscordBotPrefix}help 命令来查看帮助。");

            ImGui.Dummy(new Vector2(10, 10));

            ImGui.InputText("输入机器人Token", ref this.token, 100);
            ImGui.InputText("输入你的Discord用户名(不是昵称)", ref this.username, 50);

            ImGui.Dummy(new Vector2(10, 10));
            
            if (ImGui.Checkbox("使用代理", ref this.useProxy))
            {
                changedProxy = true;
            }
            if (this.useProxy)
            {
                if (ImGui.Checkbox("使用系统代理", ref this.useSystemProxy))
                {
                   changedProxy = true;
                }
                if (!this.useSystemProxy)
                {
                    changedProxy = true;
                    if (ImGui.InputText("代理地址", ref this.proxyAddress, 100))
                    {
                        changedProxy = true;
                    }
                }
            }

            if (changedProxy)
            {
                ImGui.TextColored(errorColor, "代理更改后，需要重新启动插件来生效。");
            }

            ImGui.Text("状态: ");
            ImGui.SameLine();

            var message = this.Plugin.Discord.State switch
            {
                DiscordState.None => "未启动",
                DiscordState.Ready => "已连接!",
                DiscordState.TokenInvalid => "Token不合法或未填写。",
                DiscordState.BadNetwork => "网络连接失败。",
                _ => "Unknown"
            };

            ImGui.TextColored(this.Plugin.Discord.State == DiscordState.Ready ? fineColor : errorColor, message);
            if (this.Plugin.Discord.State == DiscordState.Ready && ImGui.Button("添加到我的Server"))
            {
                Process.Start(
                    new ProcessStartInfo { 
                        FileName = $"https://discordapp.com/oauth2/authorize?client_id={this.Plugin.Discord.UserId}&scope=bot&permissions=2684742720", UseShellExecute = true 
                    } 
                );
            }

            ImGui.Dummy(new Vector2(10, 10));

            if (ImGui.Button("帮助"))
            {
                Process.Start(
                    new ProcessStartInfo
                    {
                        FileName = Constant.HelpLink,
                        UseShellExecute = true
                    } 
                );
            }

            ImGui.SameLine();
            if (disableSave)
            {
                ImGui.BeginDisabled();
            }
            if (ImGui.Button("保存"))
            {
                disableSave = true;
                Logger.Verbose("Reloading Discord...");

                this.Plugin.Config.DiscordToken = this.token;
                this.Plugin.Config.DiscordOwnerName = this.username;
                this.Plugin.Config.UseProxy = this.useProxy;
                this.Plugin.Config.UseSystemProxy = this.useSystemProxy;
                this.Plugin.Config.ProxyAddress = this.proxyAddress;
                this.Plugin.Config.Save();
                Task.Run(async () =>
                {
                    this.Plugin.Discord.Dispose();
                    this.Plugin.Discord = new DiscordHandler(this.Plugin);
                    await this.Plugin.Discord.Start();
                    disableSave = false;
                });
            }
            if (disableSave)
            {
                ImGui.EndDisabled();
            }
        }
    }
}
