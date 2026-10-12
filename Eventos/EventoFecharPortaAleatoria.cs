using System;
using System.Collections.Generic;
using System.Reflection;

namespace YouTubeVoteBridge;

internal sealed class EventoFecharPortaAleatoria : EventoVotacao
{
    private static readonly Random Sorteador = new();
    public override string Nome => "Fechar uma porta aleatória";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || ShipStatus.Instance == null) return;
        try
        {
            List<OpenableDoor> portas = new();
            foreach (OpenableDoor porta in ShipStatus.Instance.AllDoors)
                if (porta != null && porta.Room != SystemTypes.Lounge && porta.Room != SystemTypes.Decontamination) portas.Add(porta);
            if (portas.Count == 0)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("Evento de porta aleatória: nenhuma porta válida encontrada.");
                return;
            }
            OpenableDoor escolhida = portas[Sorteador.Next(portas.Count)];
            escolhida.SetDoorway(false);
            bool marcouSistema = false;
            if (ShipStatus.Instance.Systems.TryGetValue(SystemTypes.Doors, out ISystemType sistema))
            {
                PropertyInfo propriedade = sistema.GetType().GetProperty("IsDirty", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (propriedade != null && propriedade.CanWrite)
                { propriedade.SetValue(sistema, true); marcouSistema = true; }
            }
            if (!marcouSistema)
            {
                ShipStatus.Instance.CloseDoorsOfType(escolhida.Room);
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("IsDirty não é gravável; usado fechamento de sala como fallback, que pode fechar outras portas da sala.");
            }
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("Evento de porta aleatória: porta Id=" + escolhida.Id + ", sala=" + escolhida.Room + ", sincronização solicitada.");
        }
        catch (Exception ex)
        { YouTubeVoteBridgePlugin.Instance?.Log.LogError("Falha ao fechar uma porta aleatória: " + ex); }
    }
}
