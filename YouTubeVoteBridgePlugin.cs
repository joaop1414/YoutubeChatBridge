// Segue o padrão do EHR: adiciona os tipos extras no Prefix de RpcSetTasks,
// antes que a lista seja enviada aos clientes vanilla.
[HarmonyPatch(typeof(NetworkedPlayerInfo), nameof(NetworkedPlayerInfo.RpcSetTasks))]
public static class YouTubeVoteAdicionarTarefasPrefixPatch
{
    public static void Prefix(NetworkedPlayerInfo __instance, ref Il2CppStructArray<byte> __0)
    {
        EventoAdicionarTarefas.AplicarAdicoesAntesDeEnviar(__instance, ref __0);
    }
}

// O evento de adicionar tarefas chama NetworkedPlayerInfo.RpcSetTasks.
 // Quando essa lista é aplicada, tentamos restaurar as tarefas que já estavam concluídas.
[HarmonyPatch]
public static class YouTubeVoteAdicionarTarefasRestauracaoPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        string[] nomes = { "RpcSetTasks", "SetTasks" };
        foreach (string nome in nomes)
        {
            MethodBase metodo = AccessTools.Method(typeof(NetworkedPlayerInfo), nome);
            if (metodo != null)
                yield return metodo;
        }
    }

    public static void Postfix(NetworkedPlayerInfo __instance)
    {
        if (__instance == null || PlayerControl.AllPlayerControls == null)
            return;

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null)
                continue;

            if (jogador.Data == __instance)
            {
                EventoAdicionarTarefas.AgendarRestauracaoTarefasConcluidas(jogador);
                return;
            }
        }
    }
}

// EHR intercepta CheckShapeshift para identificar a habilidade real, antes do RPC
// visual. Capturamos as tarefas nesse ponto para não depender somente de RpcShapeshift.
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CheckShapeshift))]
public static class YouTubeVoteShapeshiftCheckPatch
{
    public static void Prefix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target)
    {
        if (__instance == null || target == null)
            return;

        // O log do próprio jogo confirma que CheckShapeshift é chamado com
        // target == jogador quando a habilidade está revertendo.
        if (__instance.PlayerId == target.PlayerId)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"Shapeshift: CheckShapeshift detectou DEStransformação para PlayerId={__instance.PlayerId}; aguardando RpcShapeshift/SetTasks para restaurar tarefas.");
            YouTubeVoteBridgePlugin.RegistrarDestransformacaoShapeshifter(__instance);
            return;
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"Shapeshift detectado via CheckShapeshift (padrão EHR): PlayerId={__instance.PlayerId}, alvo={target.PlayerId}.");
        YouTubeVoteBridgePlugin.RegistrarTarefasAntesDoShapeshift(__instance, target);
    }
}


// ============================================================
// MENSAGENS DE JOGADORES COM TAG
// ============================================================
// Não adicionamos quebra de linha às mensagens do chat dos jogadores tagueados.
// A tag continua apenas no nome do jogador; o texto enviado fica intacto.


// ============================================================
// CONTROLE DO FIM DE JOGO
// ============================================================
// Quando a opção está ativa, bloqueia chamadas normais do jogo para
// GameManager.RpcEndGame. O mod pode autorizar explicitamente um fim
// de jogo próprio usando ControleFimDeJogo.PermitirFimDoMod.
internal static class ControleFimDeJogo
{
    internal static bool PermitirFimDoMod;
}

[HarmonyPatch]
internal static class YouTubeVoteBridgeBloquearFimDeJogoOriginalPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        return typeof(GameManager).GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m => m.Name == "RpcEndGame")
            .Where(m =>
            {
                ParameterInfo[] p = m.GetParameters();
                return p.Length == 2 &&
                    p[0].ParameterType.IsEnum &&
                    p[1].ParameterType == typeof(bool);
            });
    }

    private static bool Prefix(MethodBase __originalMethod)
    {
        if (YouTubeVoteBridgePlugin.SemFimDeJogo?.Value != true ||
            ControleFimDeJogo.PermitirFimDoMod)
            return true;

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[Fim de jogo] Encerramento original bloqueado pela configuração 'Sem fim de jogo' (" +
            __originalMethod.Name + ").");
        return false;
    }
}


// EHR detecta o estado no Postfix de RpcShapeshift: alvo != jogador = shift;
// alvo == jogador = revert. Não reinterceptamos HandleRpc, pois isso pode impedir
// o jogo de processar o evento original.
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RpcShapeshift))]
public static class YouTubeVoteRainbowShapeshiftPatch
{
    public static void Prefix(
        PlayerControl __instance,
        [HarmonyArgument(0)] PlayerControl target)
    {
        if (__instance == null || target == null) return;

        if (__instance.PlayerId == target.PlayerId)
        {
            // No protocolo do jogo, alvo == próprio jogador representa destransformação.
            YouTubeVoteBridgePlugin.RegistrarDestransformacaoShapeshifter(__instance);
        }
        else
        {
            YouTubeVoteBridgePlugin.RegistrarTarefasAntesDoShapeshift(__instance, target);
        }
    }

    public static void Postfix(
        PlayerControl __instance,
        [HarmonyArgument(0)] PlayerControl target,
        [HarmonyArgument(1)] bool shouldAnimate)
    {
        if (__instance == null || target == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "Shapeshift: RpcShapeshift executou, mas instance ou target é null.");
            return;
        }

        if (__instance.PlayerId == target.PlayerId)
        {
            // Segunda tentativa: alguns builds atualizam myTasks dentro do RPC,
            // antes de executar o Postfix. Se SetTasks rodar depois, o patch da lista
            // fará outra tentativa, pois o estado aguardando restauração permanece.
            YouTubeVoteBridgePlugin.RestaurarTarefasDepoisDeAtualizar(__instance);
        }

        bool rainbow = YouTubeVoteBridgePlugin.JogadorTemRainbowParaDestransformacao(__instance);
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"Rainbow diagnóstico: RpcShapeshift executou; PlayerId={__instance.PlayerId}, alvo={target.PlayerId}, shouldAnimate={shouldAnimate}, rainbowAtivo={rainbow}.");

        if (!rainbow)
            return;

        YouTubeVoteBridgePlugin.DefinirEstadoShapeshiftRainbow(
            __instance, __instance.PlayerId != target.PlayerId, target, shouldAnimate);
    }
}

// Protege o nome temporário da ejeção contra chamadas diretas de SetName
// feitas por outras rotinas do mod no host.
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.SetName))]
public static class YouTubeVoteEjectionBlockOtherNamesPatch
{
    public static bool Prefix(PlayerControl __instance, string __0)
    {
        return YouTubeVoteBridgePlugin.PodeAlterarNomeDuranteEjecao(__instance, __0);
    }
}

// Begin recebe os dados de quem foi ejetado. O Prefix muda o nome global
// antes de a tela/animação de ejeção começar.
[HarmonyPatch]
public static class YouTubeVoteEjectionGlobalNamePatch
{
    private static MethodBase TargetMethod()
    {
        Type tipo = AccessTools.TypeByName("ExileController");
        return tipo == null ? null : AccessTools.Method(tipo, "Begin");
    }

    public static void Prefix(object[] __args)
    {
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"Ejeção: ExileController.Begin chamado; argumentos={__args?.Length ?? 0}.");
        if (__args == null) return;
        foreach (object arg in __args)
        {
            if (arg == null) continue;
            object id = YouTubeVoteBridgePlugin.LerMembroPublico(arg, "PlayerId", "playerId", "PlayerID");
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"Ejeção diagnóstico: argumento tipo={arg.GetType().FullName}, PlayerId={id ?? "não encontrado"}.");
            // Begin recebe InitProperties, que encapsula a informação do jogador.
            // Passamos o objeto mesmo sem PlayerId direto para a rotina procurar os membros internos.
            YouTubeVoteBridgePlugin.PrepararNomeGlobalDeEjecao(arg);
            if (id != null)
                return;
        }
        YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
            "Ejeção: Begin foi chamado, mas nenhum argumento com PlayerId e Role foi encontrado.");
    }
}

[HarmonyPatch]
public static class YouTubeVoteEjectionRestoreNamePatch
{
    private static MethodBase TargetMethod()
    {
        Type tipo = AccessTools.TypeByName("ExileController");
        return tipo == null ? null : AccessTools.Method(tipo, "WrapUp");
    }

    public static void Postfix()
    {
        YouTubeVoteBridgePlugin.RestaurarNomeGlobalDeEjecao();
    }
}

// Corrige a tag na lista de votação da reunião. A interface do host é local e pode
// reconstruir os nomes sem receber o SetName que ele próprio enviou.
[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Update))]
public static class YouTubeVoteRainbowMeetingNamePatch
{
    public static void Postfix()
    {
        YouTubeVoteBridgePlugin.AtualizarTagRainbowNaReuniao();
    }
}
