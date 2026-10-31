using AmongUs.InnerNet.GameDataMessages;
using Hazel;
using InnerNet;
using UnityEngine;

namespace YouTubeVoteBridge;

// Implementação local e mínima do empacotador usado pelo Hydra.
// BatchedMessage não faz parte das bibliotecas do Among Us: no Hydra,
// ele é uma classe própria do projeto HydraMenu.network.
internal sealed class BatchedMessage
{
    private readonly MessageWriter writer;
    private int messageCount;

    internal BatchedMessage()
    {
        writer = MessageWriter.Get(SendOption.Reliable);
        writer.StartMessage(InnerNet.Tags.GameData);
        writer.Write(AmongUsClient.Instance.GameId);
    }

    internal void QueueSpawn(InnerNetObject netObject, int ownerId, SpawnFlags flags)
    {
        SpawnGameDataMessage spawn =
            AmongUsClient.Instance.CreateSpawnMessage(netObject, ownerId, flags);

        spawn.Serialize(writer);
        messageCount++;
    }

    internal void QueueDespawn(InnerNetObject netObject)
    {
        if (netObject == null)
            return;

        // Guardamos o NetId antes de RemoveNetObject, pois o método pode
        // invalidá-lo no objeto local.
        uint netId = netObject.NetId;

        UnityEngine.Object.Destroy(netObject.gameObject);
        AmongUsClient.Instance.RemoveNetObject(netObject);

        writer.StartMessage((byte)GameDataTypes.DespawnFlag);
        writer.WritePacked(netId);
        writer.EndMessage();

        messageCount++;
    }

    internal void FinishBatch()
    {
        try
        {
            writer.EndMessage();

            if (messageCount > 0)
                AmongUsClient.Instance.SendOrDisconnect(writer);
        }
        finally
        {
            writer.Recycle();
        }
    }
}
