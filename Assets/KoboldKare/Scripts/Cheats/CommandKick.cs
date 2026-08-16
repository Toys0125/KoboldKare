using System.Collections.Generic;
using System.Text;
using KoboldKare.Basis.Networking;
using Photon.Pun;

[System.Serializable]
public class CommandKick : Command {
    public override string GetArg0() => "/kick";

    public override void Execute(StringBuilder output, Kobold k, string[] args) {
        base.Execute(output, k, args);
        if (args.Length != 2) {
            throw new CheatsProcessor.CommandException("Usage: /kick {actor number}");
        }
        if (!int.TryParse(args[1], out int actorNum)) {
            throw new CheatsProcessor.CommandException("Must use actor number to identify player, use `/list players` to find that.");
        }
        if (k != (Kobold)PhotonNetwork.LocalPlayer.TagObject || !PhotonNetwork.IsMasterClient) {
            throw new CheatsProcessor.CommandException("Not allowed to kick players.");
        }
        if (actorNum <= 0 || actorNum > ushort.MaxValue) {
            throw new CheatsProcessor.CommandException($"No player found with id {actorNum}, use `/list players`.");
        }

        foreach (var player in PhotonNetwork.PlayerList) {
            if (player.ActorNumber != actorNum) {
                continue;
            }
            if (Equals(player, PhotonNetwork.LocalPlayer)) {
                throw new CheatsProcessor.CommandException("Don't kick yourself :(");
            }

            KoboldKareSessionCoordinator coordinator = KoboldKareSessionCoordinator.Instance;
            if (coordinator == null || !coordinator.KickPlayer((ushort)actorNum, "Removed by the server host.")) {
                throw new CheatsProcessor.CommandException("Basis could not send the kick request.");
            }
            return;
        }
        throw new CheatsProcessor.CommandException($"No player found with id {actorNum}, use `/list players`.");
    }

    public override IEnumerable<AutocompleteResult> Autocomplete(int argumentIndex, string[] arguments, string text) {
        if (argumentIndex != 1) {
            yield break;
        }

        foreach (var player in PhotonNetwork.PlayerList) {
            if (Equals(player, PhotonNetwork.LocalPlayer)) {
                continue;
            }
            yield return new AutocompleteResult(player.NickName, player.ActorNumber.ToString());
        }
    }
}
