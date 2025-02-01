using Dalamud.Game;
using Dalamud.IoC;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Dalamud.DiscordBridge.XivApi
{
    public unsafe class ChatSender
    {

        private delegate void SendChatDelegate(UIModule* @this, Utf8String* message, Utf8String* historyMessage, bool pushToHistory);


        private readonly SendChatDelegate sendChat = null!;

        const string SendChat = "48 89 5C 24 ?? 57 48 83 EC 20 48 8B FA 48 8B D9 45 84 C9";

        public ChatSender(ISigScanner sigScanner)
        {
            if (sigScanner.TryScanText(SendChat, out var sendChatPtr))
            {
                sendChat = Marshal.GetDelegateForFunctionPointer<SendChatDelegate>(sendChatPtr);
            }
        }

        public void SendMessage(string message)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(message);
            var str = Utf8String.FromString(message);
            str->SanitizeString(0x27F, null);
            sendChat(UIModule.Instance(), str, null, false);
            str->Dtor(true);
        }
    }
}
