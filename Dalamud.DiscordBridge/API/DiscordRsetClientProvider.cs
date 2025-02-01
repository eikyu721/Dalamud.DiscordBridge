using Dalamud.DiscordBridge;
using Dalamud.Utility;
using Discord.Net.Rest;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Dalamud.DiscordBridgeFork.API
{
    internal class DiscordRsetClientProvider
    {
        public static readonly RestClientProvider Instance = Create(GetProxy());

        /// <exception cref="PlatformNotSupportedException">The default RestClientProvider is not supported on this platform.</exception>
        public static RestClientProvider Create(IWebProxy webProxy = null)
        {
            return url =>
            {
                try
                {
                    return new DiscordRestClient(url, webProxy != null, webProxy);
                }
                catch (PlatformNotSupportedException ex)
                {
                    throw new PlatformNotSupportedException("The default RestClientProvider is not supported on this platform.", ex);
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
