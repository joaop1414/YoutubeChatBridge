using System;
using System.Reflection;
using HarmonyLib;

namespace YouTubeVoteBridge;

/// <summary>
/// Revela a função diretamente no texto da tela de ejeção.
/// Segue o ciclo do EHR: usa o jogador efetivamente enviado para ExileController.Begin
/// e altera completeString, sem renomear o jogador via SetName durante a votação.
/// </summary>
[HarmonyPatch]
internal static class YouTubeVoteEjectionCompleteStringPatch
{
    private static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(ExileController), "Begin");

    private static void Postfix(ExileController __instance, object[] __args)
    {
        if (__instance == null)
            return;

        try
        {
            object initData = LerMembro(__instance, "initData", "InitData");
            object info = LerMembro(initData, "networkedPlayer", "NetworkedPlayer",
                "exiled", "Exiled", "playerInfo", "PlayerInfo");

            if (info == null && __args != null)
            {
                foreach (object argumento in __args)
                {
                    if (argumento == null) continue;
                    object candidato = LerMembro(argumento, "networkedPlayer", "NetworkedPlayer",
                        "exiled", "Exiled", "playerInfo", "PlayerInfo");
                    if (candidato != null)
                    {
                        info = candidato;
                        break;
                    }
                }
            }

            object idValor = LerMembro(info, "PlayerId", "playerId", "PlayerID");
            if (idValor == null)
                return;

            byte id = Convert.ToByte(idValor);
            PlayerControl jogador = null;
            if (PlayerControl.AllPlayerControls != null)
            {
                foreach (PlayerControl candidato in PlayerControl.AllPlayerControls)
                {
                    if (candidato != null && candidato.PlayerId == id)
                    {
                        jogador = candidato;
                        break;
                    }
                }
            }

            if (jogador == null || jogador.Data == null)
                return;

            string nomeRede = jogador.Data.PlayerName ?? string.Empty;
            string nomeOriginal = SistemaNomesLocais.ObterNomeBase(jogador);
            string funcao = SistemaNomesLocais.ObterNomeFuncao(jogador);

            bool impostor = false;
            try { impostor = jogador.Data.Role != null && jogador.Data.Role.IsImpostor; }
            catch
            {
                string tipo = jogador.Data.RoleType.ToString();
                impostor = tipo.IndexOf("Impostor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tipo.Equals("Phantom", StringComparison.OrdinalIgnoreCase) ||
                    tipo.Equals("Viper", StringComparison.OrdinalIgnoreCase) ||
                    tipo.Equals("Shapeshifter", StringComparison.OrdinalIgnoreCase);
            }

            string cor = impostor ? "#FF4040" : "#00EAFF";
            string nomeRevelado = nomeOriginal +
                " era <color=" + cor + ">" + funcao + "</color>";

            string texto = __instance.completeString ?? string.Empty;
            bool substituiu = false;

            if (!string.IsNullOrEmpty(nomeRede) && texto.Contains(nomeRede, StringComparison.Ordinal))
            {
                __instance.completeString = texto.Replace(nomeRede, nomeRevelado, StringComparison.Ordinal);
                substituiu = true;
            }
            else
            {
                object nomeInfo = LerMembro(info, "PlayerName", "playerName", "Name");
                string nomeInfoTexto = nomeInfo?.ToString();

                if (!string.IsNullOrEmpty(nomeInfoTexto) &&
                    texto.Contains(nomeInfoTexto, StringComparison.Ordinal))
                {
                    __instance.completeString = texto.Replace(nomeInfoTexto, nomeRevelado, StringComparison.Ordinal);
                    substituiu = true;
                }
                else if (!string.IsNullOrEmpty(nomeOriginal) &&
                    texto.Contains(nomeOriginal, StringComparison.Ordinal))
                {
                    // Fallback para versões que já formatam/limpam PlayerName
                    // antes de preencher completeString.
                    __instance.completeString = texto.Replace(nomeOriginal, nomeRevelado, StringComparison.Ordinal);
                    substituiu = true;
                }
            }

            if (substituiu)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[Ejeção/EHR] Texto de ejeção atualizado em Begin. PlayerId=" +
                    id + ", função=" + funcao + ".");
            }
            else
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[Ejeção/EHR] completeString não continha o nome original; texto vanilla preservado. " +
                    "PlayerId=" + id + ", completeString='" + texto + "'.");
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Ejeção/EHR] Falha ao atualizar completeString em Begin: " + ex);
        }
    }

    private static object LerMembro(object alvo, params string[] nomes)
    {
        if (alvo == null) return null;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type tipo = alvo.GetType();

        foreach (string nome in nomes)
        {
            try
            {
                FieldInfo campo = tipo.GetField(nome, flags);
                if (campo != null) return campo.GetValue(alvo);

                PropertyInfo propriedade = tipo.GetProperty(nome, flags);
                if (propriedade != null && propriedade.GetIndexParameters().Length == 0)
                    return propriedade.GetValue(alvo);
            }
            catch { }
        }

        return null;
    }
}
