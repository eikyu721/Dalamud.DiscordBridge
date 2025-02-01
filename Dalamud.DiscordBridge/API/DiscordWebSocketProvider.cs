using Dalamud.DiscordBridge;
using Dalamud.Utility;
using Discord.Net.WebSockets;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Dalamud.DiscordBridgeFork.API
{
    public static class DiscordWebSocketProvider
    {
        public static readonly WebSocketProvider Instance = Create(GetProxy());

        /// <exception cref="PlatformNotSupportedException">The default WebSocketProvider is not supported on this platform.</exception>
        public static WebSocketProvider Create(IWebProxy proxy = null)
        {
            return () =>
            {
                try
                {
                    return new DiscordWebSocketClient(proxy);
                }
                catch (PlatformNotSupportedException ex)
                {
                    throw new PlatformNotSupportedException("The default WebSocketProvider is not supported on this platform.", ex);
                }
            };
        }

        private static IWebProxy GetProxy()
        {
            if (!DiscordBridgePlugin.Plugin.Config.UseProxy)
            {
                return null;
            }
            string proxyServer = DiscordBridgePlugin.Plugin.Config.UseSystemProxy ? GetRegKey() : DiscordBridgePlugin.Plugin.Config.ProxyAddress;
            if (proxyServer == null || proxyServer.IsNullOrEmpty())
            {
                return null;
            }
            Service.Logger.Info("Using system proxy: " + proxyServer);
            return new WebProxy(proxyServer);
        }

        private static string GetRegKey()
        {
            try
            {
                // 打开注册表项
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
                {
                    if (key != null)
                    {
                        // 检查是否启用了代理
                        int proxyEnabled = (int)key.GetValue("ProxyEnable", 0);
                        if (proxyEnabled == 1)
                        {
                            // 获取代理服务器地址
                            string proxyServer = key.GetValue("ProxyServer") as string;
                            return $"http://{proxyServer}";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Service.Logger.Warning("读取注册表时出错: " + ex.Message);
            }
            return null;
        }
    }
}
