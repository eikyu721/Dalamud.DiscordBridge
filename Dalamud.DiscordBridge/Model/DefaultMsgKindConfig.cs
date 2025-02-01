using Dalamud.Game.Text;
using Dalamud.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Dalamud.DiscordBridge.Model
{
    public class DefaultMsgKindConfig
    {
        public XivChatType ChatType { get; private set;} = XivChatType.None;
        public string TellTargetStr { get; private set; } = "";

        public DefaultMsgKindConfig(XivChatType chatType, string tellTargetStr = "")
        {
            this.ChatType = chatType;
            this.TellTargetStr = tellTargetStr;
        }
    }
}
