using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using BepInEx;
using RoR2;
using R2API.Utils;

namespace VoteCommands
{
    [BepInDependency("com.bepis.r2api")]
    [NetworkCompatibility(CompatibilityLevel.NoNeedForSync)]
    [BepInPlugin("com.Rayss.VoteKick", "VoteCommands", "1.2.0")]
    public class VoteCommands : BaseUnityPlugin
    {

        public bool _voteInProgress = false;
        public List<NetworkUserId> VotedForPlayers { get; set; } = new List<NetworkUserId>();  // Count players that voted to kick
        public List<ulong> KickedPlayerSteamIds { get; set; } = new List<ulong>();

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Code Quality", "IDE0051:Remove unused private members")]
        public void Awake()
        {
            StartHooks();
        }

        private void StartHooks()
        {
            On.RoR2.Console.RunCmd += ChatStartVote;
            On.RoR2.NetworkUser.UpdateUserName += CheckSteamID;
        }

        private void ChatStartVote(On.RoR2.Console.orig_RunCmd orig, RoR2.Console self, RoR2.Console.CmdSender sender, string concommandName, List<string> userArgs)
        {
            orig(self, sender, concommandName, userArgs);

            if (concommandName.Equals("say", StringComparison.InvariantCultureIgnoreCase))
            {
                var userInput = userArgs.FirstOrDefault().Split(' ');  // Splits the content of say cmd into 2+ args: Whether it contains votekick, and the kickUser (with each additional arg for names with whitespaces)
                var chatCommand = userInput.FirstOrDefault();  // Checks for either vote_kick or votekick
                if (chatCommand.IsNullOrWhiteSpace())
                {
                    return;
                }
                else if (chatCommand.Equals("y", StringComparison.InvariantCultureIgnoreCase) && _voteInProgress)
                {
                    CountUserVote(sender.networkUser);
                }
                else if (chatCommand.Equals("vote_kick", StringComparison.InvariantCultureIgnoreCase) || chatCommand.Equals("votekick", StringComparison.InvariantCultureIgnoreCase))
                {
                    if (_voteInProgress)
                    {
                        Chat.SendBroadcastChat(new Chat.SimpleChatMessage
                        {
                            baseToken = "<color=red>Hold your horses. There's already a vote in progress.</color>"
                        });
                        return;
                    }
                    var kickUser = string.Join(" ", userInput.Skip(1));
                    if (kickUser == "")
                    {
                        Chat.SendBroadcastChat(new Chat.SimpleChatMessage
                        {
                            baseToken = "<color=red>Gotta input a player name or number.</color>"
                        });
                        return;
                    }
                    var kickUserNetworkUser = GetNetworkUserFromName(kickUser);
                    if (kickUserNetworkUser == null) { return; }  // There is a nicer way to do this, just have to learn about it. I think it's that whole ?? thing
                    StartCoroutine(WaitForVotesKick(sender, kickUserNetworkUser));
                }
                else if (chatCommand.Equals("vote_restart", StringComparison.InvariantCultureIgnoreCase) || chatCommand.Equals("voterestart", StringComparison.InvariantCultureIgnoreCase))
                {
                    if (_voteInProgress)
                    {
                        Chat.SendBroadcastChat(new Chat.SimpleChatMessage
                        {
                            baseToken = "<color=red>Hold your horses. There's already a vote in progress.</color>"
                        });
                        return;
                    }
                    StartCoroutine(WaitForVotesRestart(sender));
                }
            }
        }

        private void CheckSteamID(On.RoR2.NetworkUser.orig_UpdateUserName orig, NetworkUser self)
        {
            orig(self);
            ulong steamId = self.id.steamId.value;
            if (KickedPlayerSteamIds.Contains(steamId))
            {
                StartCoroutine(WaitToKick(self));
            }
        }

        // INCREDIBLY JANKY BUT IT WORKS FOR NOW SO FUCK IT
        private IEnumerator WaitToKick(NetworkUser kickUser)
        {
            yield return new WaitForSeconds(0.5f);
            NetworkConnection kickUserConn = FindNetworkConnectionFromNetworkUser(kickUser);
            var kickReason = new RoR2.Networking.GameNetworkManager.SimpleLocalizedKickReason("KICK_REASON_KICK", Array.Empty<string>());
            try
            {
                RoR2.Networking.GameNetworkManager.singleton.ServerKickClient(kickUserConn, kickReason);
            }
            catch
            {
#if DEBUG
                Debug.Log("DEBUG_VOTEKICK: There was a problem kicking this person. I can't into exception handling");
#endif
            }
        }

        // TODO: Change system to count down from number of votes needed, if that number is hit before the timer runs out
        private IEnumerator WaitForVotesKick(RoR2.Console.CmdSender sender, NetworkUser kickUserNetworkUser)
        {
            var kickUserSteamId = kickUserNetworkUser.id.steamId.value;
            KickedPlayerSteamIds.Add(kickUserSteamId);  // Replaces AddIdToKickListSteam(), reducing repeat code
            Chat.SendBroadcastChat(new Chat.SimpleChatMessage
            {
                baseToken = "<color=red>Vote to kick " + kickUserNetworkUser.userName + " has begun. In the next 45 seconds, Type 'Y' to vote to kick.</color>"
            });
            CountUserVote(sender.networkUser);
            int playerCount = NetworkUser.readOnlyInstancesList.Count;  // TODO: Look into changing for participating players only?
            _voteInProgress = true;
            yield return new WaitForSeconds(15f);
            Chat.SendBroadcastChat(new Chat.SimpleChatMessage
            {
                baseToken = "<color=red>30 seconds remaining in vote to kick " + kickUserNetworkUser.userName + ".</color>"
            });
            yield return new WaitForSeconds(20f);
            Chat.SendBroadcastChat(new Chat.SimpleChatMessage
            {
                baseToken = "<color=red>10 seconds remaining in vote to kick " + kickUserNetworkUser.userName + ".</color>"
            });
            yield return new WaitForSeconds(10f);
            if (VotedForPlayers.Count > (int)(playerCount * 0.51f))
            {
                Chat.SendBroadcastChat(new Chat.SimpleChatMessage
                {
                    baseToken = "<color=red>Vote to kick " + kickUserNetworkUser.userName + " has passed. Bye bye.</color>"
                });
#if DEBUG
                Debug.Log("DEBUG_VOTEKICK: Vote to kick " + kickUserNetworkUser.userName + " has passed.");  // DEBUG
#endif
                CustomKick(kickUserNetworkUser);
            }
            else
            {
                Chat.SendBroadcastChat(new Chat.SimpleChatMessage
                {
                    baseToken = "<color=red>Vote to kick " + kickUserNetworkUser.userName + " has failed.</color>"
                });
#if DEBUG
                Debug.Log("DEBUG_VOTEKICK: Vote to kick " + kickUserNetworkUser.userName + " has failed.");  // DEBUG
#endif
                KickedPlayerSteamIds.Remove(kickUserSteamId);  // Replaces RemoveIdFromKickListSteam(), reducing repeat code and not crashing by referencing the existing variable
            }
            _voteInProgress = false;
            VotedForPlayers.Clear();
        }

        // TODO: Merge this function with WaitForVotesKick, just only accept kickuser args under the circumstance that the votekick command is passed
        private IEnumerator WaitForVotesRestart(RoR2.Console.CmdSender sender)
        {
            if (!Run.instance)
            {
                Chat.SendBroadcastChat(new Chat.SimpleChatMessage
                {
                    baseToken = "<color=red>You can't start a vote to restart a run when there is no run to restart, silly goose.</color>"
                });
                yield break;
            }
            Chat.SendBroadcastChat(new Chat.SimpleChatMessage
            {
                baseToken = "<color=red>Vote to restart the run has begun. In the next 45 seconds, Type 'Y' to vote to restart.</color>"
            });
            CountUserVote(sender.networkUser);
            int playerCount = NetworkUser.readOnlyInstancesList.Count;
            _voteInProgress = true;
            yield return new WaitForSeconds(15f);
            Chat.SendBroadcastChat(new Chat.SimpleChatMessage
            {
                baseToken = "<color=red>30 seconds remaining in vote to restart.</color>"
            });
            yield return new WaitForSeconds(20f);
            Chat.SendBroadcastChat(new Chat.SimpleChatMessage
            {
                baseToken = "<color=red>10 seconds remaining in vote to restart.</color>"
            });
            yield return new WaitForSeconds(10f);
            if (VotedForPlayers.Count > (int)(playerCount * 0.51f))
            {
                Chat.SendBroadcastChat(new Chat.SimpleChatMessage
                {
                    baseToken = "<color=red>Vote to restart has passed. Heading to lobby now.</color>"
                });
#if DEBUG
                Debug.Log("DEBUG_VOTERESTART: Vote to restart has passed.");  // DEBUG
#endif
                yield return new WaitForSeconds(3f);
                RoR2.Console.instance.SubmitCmd(new RoR2.Console.CmdSender(), "run_end");  // TODO: Consider changing to ending the run normally, so everyone can see the endgame screen
            }
            else
            {
                Chat.SendBroadcastChat(new Chat.SimpleChatMessage
                {
                    baseToken = "<color=red>Vote to restart has failed.</color>"
                });
#if DEBUG
                Debug.Log("DEBUG_VOTERESTART: Vote to restart has failed.");  // DEBUG
#endif
            }
            _voteInProgress = false;
            VotedForPlayers.Clear();
        }

        private void CountUserVote(NetworkUser netUser)
        {
            NetworkUserId network_id = netUser.Network_id;
            if (VotedForPlayers.Contains(network_id)) return;  // TODO: Send private message to user that says something like "Vote already cast"
            VotedForPlayers.Add(network_id);
            if (VotedForPlayers.Count == 1)
            {
                Chat.SendBroadcastChat(new Chat.SimpleChatMessage
                {
                    baseToken = "<color=red>1 vote</color>"
                });
            }
            else
            {
                Chat.SendBroadcastChat(new Chat.SimpleChatMessage
                {
                    baseToken = "<color=red>" + VotedForPlayers.Count.ToString() + " votes</color>"
                });
            }

        }

        // Takes either a partial name or player number (not index, number) as an input
        private NetworkUser GetNetworkUserFromName(string userName)
        {
            // Something to keep in mind, if someone sets their name to a number, someone needs to still use their player number rather than their name if they want to kick them,
            // i.e. if you want to kick "Risk of Rayss 2", who is player 5, using "votekick 2" will start a vote to kick whoever is player 2, so instead use "votekick 5"
            if (int.TryParse(userName, out int result))
            {
                if (result <= NetworkUser.readOnlyInstancesList.Count && result >= 1)  // Done since it assumes players are going on normal list values, not index
                {
                    return NetworkUser.readOnlyInstancesList[result - 1];  // See above
                }
                Chat.SendBroadcastChat(new Chat.SimpleChatMessage
                {
                    baseToken = "<color=red>Could not find the player number " + userName + ". Try using their player name instead</color>"
                });
                return null;
            }
            else
            {
                foreach (NetworkUser n in NetworkUser.readOnlyInstancesList)
                {
                    if (n.userName.ToLower().Contains(userName.ToLower()))
                    {
                        return n;
                    }
                }
                Chat.SendBroadcastChat(new Chat.SimpleChatMessage
                {
                    baseToken = "<color=red>Could not find any players containing the name '" + userName + "'. Find their player number using the 'players' command and enter that instead</color>"
                });
                return null;
            }
        }

        private void CustomKick(NetworkUser nu)
        {
            try
            {
                NetworkConnection client = null;
                foreach (var connection in NetworkServer.connections)
                {
                    if (nu.connectionToClient == connection)
                    {
                        client = connection;
                        break;
                    }
                }
                if (client != null)
                {
                    var reason = new RoR2.Networking.GameNetworkManager.SimpleLocalizedKickReason("KICK_REASON_KICK");
                    RoR2.Networking.GameNetworkManager.singleton.InvokeMethod("ServerKickClient", client, reason);
                }
                else
                {
#if DEBUG
                    Debug.Log("DEBUG_VOTEKICK: Couldn't find the connection associated with the user.");
#endif
                }
            }
            catch
            {
#if DEBUG
                Debug.Log("DEBUG_VOTEKICK: Player not found.");
#endif
            }
        }

        // Borrowed from R2DSE
        internal static NetworkConnection FindNetworkConnectionFromNetworkUser(NetworkUser networkUser)
        {
            foreach (var connection in NetworkServer.connections)
            {
                if (networkUser.connectionToClient == connection)
                {
                    return connection;
                }
            }
            return null;
        }
    }

}