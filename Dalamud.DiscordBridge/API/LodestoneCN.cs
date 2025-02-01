using Dalamud.DiscordBridge;
using Dalamud.DiscordBridgeFork.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Dalamud.DiscordBridgeFork.API
{
    internal class LodestoneCN : IDisposable
    {
        private HttpClient httpClient = new HttpClient();

        public void Dispose()
        {
            httpClient.Dispose();
        }

        public async Task<LodestoneCNPlayer> SearchPlayer(string name, string server)
        {
            var response = await httpClient.GetAsync($"https://apiff14risingstones.web.sdo.com/api/common/search?type=6&keywords={name}");
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var content = await response.Content.ReadAsStringAsync();

            //将json字符串的data字段的内容反序列化为LodestoneCNPlayer list
            LodestoneCNPlayers players = Newtonsoft.Json.JsonConvert.DeserializeObject<LodestoneCNPlayers>(content);
            if (players.Data == null || players.Data.Count == 0)
            {
                return null;
            }
            Service.Logger.Warning($"Found {players.Data.Count} players with the name {name} on server {server}");
            for (int i = 0; i < players.Data.Count; i++)
            {
                Service.Logger.Warning($"Player {i}: {players.Data[i].Character_Name} on {players.Data[i].Group_Name}");
            }
            LodestoneCNPlayer player = players.Data.Where(p => p.Character_Name == name && p.Group_Name == server).Any() ? players.Data.Where(p => p.Character_Name == name && p.Group_Name == server).First() : null;
            return player;
        }
    }

    internal class LodestoneCNPlayers
    {
        public int Code { get; set; }
        public string Msg { get; set; }
        public List<LodestoneCNPlayer> Data { get; set; }
    }
}
