using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace YouTubeVoteBridge;

// O voto extra é inserido antes de o resultado da reunião ser enviado.
// Assim, clientes vanilla recebem dois registros do mesmo eleitor e o
// anfitrião recalcula a ejeção usando a mesma lista de votos.
[HarmonyPatch]
[HarmonyPriority(Priority.First)]
internal static class EventoVotoDuploRpcPatch
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(MeetingHud), "RpcVotingComplete");
    }

    public static void Prefix(object[] __args)
    {
        if (!YouTubeVoteBridgePlugin.EhHost() ||
            !EventoVotoDuplo.TentarObterJogador(out byte jogadorComVotoDuplo) ||
            __args == null ||
            __args.Length < 3 ||
            __args[0] is not Il2CppStructArray<MeetingHud.VoterState> estados ||
            estados == null)
            return;

        try
        {
            int indiceVoto = -1;
            for (int i = 0; i < estados.Length; i++)
            {
                if (estados[i].VoterId == jogadorComVotoDuplo)
                {
                    indiceVoto = i;
                    break;
                }
            }

            // Se o jogador sorteado estiver morto ou não tiver votado,
            // não há voto extra nesta reunião.
            if (indiceVoto < 0)
                return;

            MeetingHud.VoterState votoOriginal = estados[indiceVoto];
            var estadosComVotoExtra =
                new Il2CppStructArray<MeetingHud.VoterState>(estados.Length + 1);

            for (int i = 0; i < estados.Length; i++)
                estadosComVotoExtra[i] = estados[i];

            estadosComVotoExtra[estados.Length] = new MeetingHud.VoterState
            {
                VoterId = votoOriginal.VoterId,
                VotedForId = votoOriginal.VotedForId
            };

            __args[0] = estadosComVotoExtra;

            // A habilidade do Juiz tem prioridade sobre o resultado normal.
            // Ainda enviamos o ícone adicional, mas preservamos a ejeção do Juiz.
            bool juizSobrescreveu = __args.Length > 3 &&
                __args[3] != null && Convert.ToBoolean(__args[3]);

            if (!juizSobrescreveu)
                RecalcularResultado(__args, estadosComVotoExtra);

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[F7/Voto duplo] Voto de PlayerId={jogadorComVotoDuplo} duplicado para o alvo {votoOriginal.VotedForId}.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Voto duplo] Não foi possível aplicar o voto adicional: " + ex);
        }
    }

    private static void RecalcularResultado(
        object[] argumentos,
        Il2CppStructArray<MeetingHud.VoterState> estados)
    {
        var contagem = new Dictionary<byte, int>();

        for (int i = 0; i < estados.Length; i++)
        {
            byte alvo = estados[i].VotedForId;

            // 254 = não votou; 255 = voto ainda não definido.
            // 253 (skip) é uma opção válida e participa da contagem.
            if (alvo >= 254)
                continue;

            contagem.TryGetValue(alvo, out int atual);
            contagem[alvo] = atual + 1;
        }

        if (contagem.Count == 0)
            return;

        int maior = contagem.Values.Max();
        List<byte> vencedores = contagem
            .Where(voto => voto.Value == maior)
            .Select(voto => voto.Key)
            .ToList();

        if (vencedores.Count != 1)
        {
            argumentos[1] = null;
            argumentos[2] = true;
            return;
        }

        byte vencedor = vencedores[0];

        if (vencedor == 253)
        {
            argumentos[1] = null;
            argumentos[2] = false;
            return;
        }

        NetworkedPlayerInfo exilado = GameData.Instance.AllPlayers
            .ToArray()
            .FirstOrDefault(info => info != null && info.PlayerId == vencedor);

        argumentos[1] = exilado;
        argumentos[2] = exilado == null;
    }
}
