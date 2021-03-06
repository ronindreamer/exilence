﻿using System;
using System.Threading.Tasks;
using Exilence.Models;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using System.Collections.Generic;
using Exilence.Helper;
using System.Linq;
using Exilence.Interfaces;
using Newtonsoft.Json;
using Exilence.Store;
using Exilence.Models.Connection;

namespace Exilence.Hubs
{
    [EnableCors("AllowAll")]
    public class PartyHub : Hub
    {
        private IDistributedCache _cache;

        private string ConnectionId => Context.ConnectionId;
        
        public PartyHub(IDistributedCache cache)
        {
            _cache = cache;
        }
                
        public async Task JoinParty(string partyName, string playerObj)
        {
            var player = CompressionHelper.Decompress<PlayerModel>(playerObj);

            // set initial id of player
            player.ConnectionID = Context.ConnectionId;

            //update ConnectionId:Partyname index
            var success = AddToIndex(partyName);

            // look for party
            var party = await _cache.GetAsync<PartyModel>($"party:{partyName}");
            if (party == null)
            {
                party = new PartyModel() { Name = partyName, Players = new List<PlayerModel> { player } };
                await _cache.SetAsync<PartyModel>($"party:{partyName}", party);
                await Clients.Caller.SendAsync("EnteredParty", CompressionHelper.Compress(party), CompressionHelper.Compress(player));
            }
            else
            {
                var oldPlayer = party.Players.FirstOrDefault(x => x.Character.Name == player.Character.Name || x.ConnectionID == player.ConnectionID);

                if (oldPlayer == null)
                {
                    party.Players.Insert(0, player);
                }
                else
                {
                    // index of old player
                    var index = party.Players.IndexOf(oldPlayer);
                    await Groups.RemoveFromGroupAsync(oldPlayer.ConnectionID, partyName);
                    party.Players[index] = player;
                }

                await _cache.SetAsync<PartyModel>($"party:{partyName}", party);
                await Clients.Caller.SendAsync("EnteredParty", CompressionHelper.Compress(party), CompressionHelper.Compress(player));
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, partyName);
            await Clients.OthersInGroup(partyName).SendAsync("PlayerJoined", CompressionHelper.Compress(player));
            await Clients.Group(partyName).SendAsync("PlayerUpdated", CompressionHelper.Compress(player));
        }

        public async Task LeaveParty(string partyName, string playerObj)
        {
            var player = CompressionHelper.Decompress<PlayerModel>(playerObj);

            var foundParty = await _cache.GetAsync<PartyModel>($"party:{partyName}");
            if (foundParty != null)
            {
                //Handle generic players if "host" left
                var genericPlayers = foundParty.Players.Where(t => t.GenericHost == player.Character.Name).ToList();
                foreach (var genericPlayer in genericPlayers)
                {
                    foundParty.Players.Remove(genericPlayer);
                    await Clients.Group(partyName).SendAsync("PlayerLeft", genericPlayer);
                }

                var foundPlayer = foundParty.Players.FirstOrDefault(x => x.ConnectionID == player.ConnectionID);

                foundParty.Players.Remove(foundPlayer);                
                var success = RemoveFromIndex();

                if (foundParty.Players.Count != 0)
                {
                    await _cache.SetAsync<PartyModel>($"party:{partyName}", foundParty);
                }
                else
                {
                    await _cache.RemoveAsync($"party:{partyName}");
                }

            }

            await Clients.OthersInGroup(partyName).SendAsync("PlayerLeft", CompressionHelper.Compress(player));
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, partyName);
        }

        public async Task UpdatePlayer(string partyName, string playerObj)
        {
            var player = CompressionHelper.Decompress<PlayerModel>(playerObj);

            var party = await _cache.GetAsync<PartyModel>($"party:{partyName}");
            if (party != null)
            {
                var index = party.Players.IndexOf(party.Players.FirstOrDefault(x => x.ConnectionID == player.ConnectionID));
                if(index != -1)
                {
                    party.Players[index] = player;
                    await _cache.SetAsync<PartyModel>($"party:{partyName}", party);
                    await Clients.Group(partyName).SendAsync("PlayerUpdated", CompressionHelper.Compress(player));
                }
            }
            else
            {
                await Clients.Group(partyName).SendAsync("ForceDisconnect");
            }
        }
        public async Task GenericUpdatePlayer(PlayerModel player, string partyName)
        {
            var party = await _cache.GetAsync<PartyModel>($"party:{partyName}");
            if (party != null)
            {
                var index = party.Players.IndexOf(party.Players.FirstOrDefault(x => x.Character.Name == player.Character.Name));

                if (index == -1)
                {
                    party.Players.Insert(0, player);
                    await Clients.Group(partyName).SendAsync("PlayerJoined", player);
                }
                else
                {
                    party.Players[index] = player;
                }

                await _cache.SetAsync<PartyModel>($"party:{partyName}", party);
                await Clients.Group(partyName).SendAsync("GenericPlayerUpdated", player);
            }
        }

        public override async Task OnDisconnectedAsync(Exception exception)
        {
            var partyName = GetPartynameFromIndex();

            if (partyName != null)
            {
                var foundParty = await _cache.GetAsync<PartyModel>($"party:{partyName}");
                var foundPlayer = foundParty.Players.FirstOrDefault(x => x.ConnectionID == Context.ConnectionId);
                if (foundPlayer != null)
                {   //This compression and then uncompression is ugly
                    await LeaveParty(partyName, CompressionHelper.Compress(foundPlayer));
                    var success = RemoveFromIndex();
                }
            }
            await base.OnDisconnectedAsync(exception);
        }

        private string GetPartynameFromIndex()
        {
            var result = ConnectionStore.ConnectionIndex.TryGetValue(ConnectionId, out var partyName);
            return result ? partyName.PartyName : null;
        }

        private bool RemoveFromIndex()
        {
            var success = ConnectionStore.ConnectionIndex.Remove(ConnectionId);
            return success;
        }

        private bool AddToIndex(string partyName)
        {
            var success = ConnectionStore.ConnectionIndex.TryAdd(ConnectionId, new ConnectionModel() { PartyName = partyName, ConnectedDate = DateTime.Now });
            return success;
        }
    }
}