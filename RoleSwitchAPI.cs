using System;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using HarmonyLib;
using Hazel;
using InnerNet;
using BepInEx.Unity.IL2CPP.Utils;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using UnityEngine;

namespace YouTubeVoteBridge;

/// <summary>
/// Troca funções somente quando solicitada explicitamente pelo host durante a partida.
/// A seleção inicial permanece inteiramente vanilla para preservar a quantidade
/// de impostores e as opções de função da sala.
/// </summary>
internal static class RoleSwitchAPI
{
    private sealed class TrocaPendente
    {
        public PlayerControl Jogador;
        public RoleTypes Funcao;

        public TrocaPendente(PlayerControl jogador, RoleTypes funcao)
        {
            Jogador = jogador;
            Funcao = funcao;
        }
    }

    private static bool loteEmAndamento;
    private static float inicioLoteRealtime;
    private static int identificadorLote;
    private const float TIMEOUT_LOTE_SEGUNDOS = 10f;

    // RoleMap inspirado no EHR: (seerId, targetId) => função que aquele
    // observador deve receber para o jogador-alvo.
    private static readonly Dictionary<(byte SeerId, byte TargetId), RoleTypes> RoleMap = new();
    private static readonly HashSet<byte> JogadoresDoMapa = new();
    private static bool mapaInicializado;

    private sealed class RoleCall
    {
        public PlayerControl Target;
        public RoleTypes Role;
        public int ClientId;

        public RoleCall(PlayerControl target, RoleTypes role, int clientId)
        {
            Target = target;
            Role = role;
            ClientId = clientId;
        }
    }

    internal static bool AplicarFuncaoGlobal(PlayerControl jogador, RoleTypes funcao)
    {
        if (jogador == null || jogador.Data == null ||
            AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost)
            return false;

        try
        {
            GarantirRoleMapInicializado();
            AtualizarRoleMapCompleto(jogador, funcao);
            RpcSetRoleGlobal(jogador, funcao, atualizarMapa: false);
            // O registro só será atualizado pelo verificador depois que
            // Data.Role.Role confirmar a troca realmente aplicada.
            SistemaNomesLocais.Atualizar();
            return true;
        }
        catch (Exception ex)
        {
            LogErro($"AplicarFuncaoGlobal falhou para PlayerId={jogador.PlayerId}: {ex}");
            return false;
        }
    }

    internal static bool TrocarFuncao(PlayerControl jogador, string nomeFuncao)
    {
        if (!Enum.TryParse(typeof(RoleTypes), nomeFuncao, true, out object valor))
        {
            LogAviso($"RoleTypes não contém a função '{nomeFuncao}'.");
            return false;
        }

        return TrocarFuncao(jogador, (RoleTypes)valor);
    }

    internal static bool TrocarFuncao(PlayerControl jogador, RoleTypes funcao)
    {
        if (jogador == null)
            return false;

        return TrocarFuncoes(new[]
        {
            new KeyValuePair<PlayerControl, RoleTypes>(jogador, funcao)
        });
    }

    /// <summary>
    /// API usada pelo controlador F8 e por recursos do mod para trocar funções
    /// explicitamente durante a partida.
    /// </summary>
    internal static bool TrocarFuncoes(
        IEnumerable<KeyValuePair<PlayerControl, RoleTypes>> atribuicoes)
    {
        List<TrocaPendente> trocas = new();
        HashSet<byte> idsAdicionados = new();

        if (atribuicoes != null)
        {
            foreach (KeyValuePair<PlayerControl, RoleTypes> item in atribuicoes)
            {
                PlayerControl jogador = item.Key;
                if (jogador == null || jogador.Data == null || jogador.Data.Disconnected)
                {
                    LogAviso("Uma atribuição foi ignorada porque o jogador é inválido ou está desconectado.");
                    continue;
                }

                if (!idsAdicionados.Add(jogador.PlayerId))
                    continue;

                trocas.Add(new TrocaPendente(jogador, item.Value));
            }
        }

        // Somente operações explícitas durante a partida chegam aqui.
        // A seleção inicial é executada integralmente pelo jogo.
        return ExecutarAtribuicoes(trocas);
    }

    private static bool ExecutarAtribuicoes(List<TrocaPendente> trocas)
    {
        AmongUsClient cliente = AmongUsClient.Instance;
        PlayerControl local = PlayerControl.LocalPlayer;

        if (cliente == null || !cliente.AmHost)
        {
            LogAviso("A atribuição de funções exige que o cliente seja o host.");
            return false;
        }

        if (RoleManager.Instance == null || local == null)
        {
            LogErro("RoleManager.Instance ou PlayerControl.LocalPlayer está nulo.");
            return false;
        }

        if (loteEmAndamento)
        {
            LogAviso("Já existe uma atribuição de funções em andamento.");
            return false;
        }

        trocas = trocas?
            .Where(x => x != null && x.Jogador != null &&
                        x.Jogador.Data != null && !x.Jogador.Data.Disconnected)
            .GroupBy(x => x.Jogador.PlayerId)
            .Select(g => g.Last())
            .ToList() ?? new List<TrocaPendente>();

        if (trocas.Count == 0)
        {
            LogAviso("Nenhuma atribuição válida foi recebida.");
            return false;
        }

        loteEmAndamento = true;
        inicioLoteRealtime = Time.realtimeSinceStartup;
        int loteAtual = ++identificadorLote;
        YouTubeVoteBridgePlugin.BloquearFimDeJogoDuranteTrocaDeFuncoes = true;

        try
        {
            // Troca global no estilo EHR: aplica localmente e transmite o RPC
            // vanilla SetRole com canOverrideRole=true para todos os clientes.
            GarantirRoleMapInicializado();

            foreach (TrocaPendente troca in trocas)
                AplicarFuncaoComRoleMap(troca.Jogador, troca.Funcao);

            AtualizarHudDaFuncaoLocal(trocas);
            SistemaNomesLocais.Atualizar();
            LogInfo($"Lote {loteAtual} enviado pelo fluxo global do EHR com {trocas.Count} atribuição(ões). Nenhum jogador será revivido ou rebaixado para Crewmate durante a troca.");
            MonoBehaviourExtensions.StartCoroutine(
                local, FinalizarLoteAposTroca(loteAtual));
            return true;
        }
        catch (Exception ex)
        {
            if (loteAtual == identificadorLote)
            {
                loteEmAndamento = false;
                YouTubeVoteBridgePlugin.BloquearFimDeJogoDuranteTrocaDeFuncoes = false;
            }

            LogErro("Não foi possível iniciar a distribuição das funções: " + ex);
            return false;
        }
    }

    private static System.Collections.IEnumerator FinalizarLoteAposTroca(int loteAtual)
    {
        // Dá tempo para CoSetRole e o RPC vanilla serem processados sem alterar
        // novamente as funções ou o estado de vida dos jogadores.
        yield return new WaitForSecondsRealtime(0.75f);

        if (loteAtual != identificadorLote)
            yield break;

        loteEmAndamento = false;
        YouTubeVoteBridgePlugin.BloquearFimDeJogoDuranteTrocaDeFuncoes = false;
        LogInfo($"Lote {loteAtual} finalizado. Funções enviadas; estado de vida original preservado.");
    }

    /// <summary>
    /// Chamado pelo Update do jogo. Recupera a API se a coroutine de restauração
    /// não avançar ou for interrompida sem executar o finally.
    /// </summary>
    internal static void ProcessarEstado()
    {
        if (!loteEmAndamento)
            return;

        float decorrido = Time.realtimeSinceStartup - inicioLoteRealtime;
        if (decorrido < TIMEOUT_LOTE_SEGUNDOS)
            return;

        int loteTravado = identificadorLote;
        identificadorLote++;
        loteEmAndamento = false;
        YouTubeVoteBridgePlugin.BloquearFimDeJogoDuranteTrocaDeFuncoes = false;

        LogErro($"Lote {loteTravado} excedeu {TIMEOUT_LOTE_SEGUNDOS:0} segundos sem finalizar. Estado liberado pelo watchdog após {decorrido:0.0}s; a rotina antiga não poderá alterar o estado de um próximo lote.");
    }

    /// <summary>
    /// Equivalente ao RpcSetRoleGlobal do EHR para funções globais.
    /// Aplica no host e transmite SetRole bruto para todos os clientes.
    /// </summary>
    private static void RpcSetRoleGlobal(PlayerControl jogador, RoleTypes funcao, bool atualizarMapa = false)
    {
        if (jogador == null || jogador.Data == null ||
            AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost)
            return;

        try
        {
            if (atualizarMapa)
            {
                GarantirRoleMapInicializado();
                AtualizarRoleMapCompleto(jogador, funcao);
            }

            // Mesmo fluxo do EHR: aplica no host sem coroutine e envia o RPC
            // vanilla SetRole para todos os clientes com canOverrideRole=true.
            if (AmongUsClient.Instance.AmClient)
            {
                jogador.SetRole(funcao, true);
            }

            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                jogador.NetId,
                (byte)RpcCalls.SetRole,
                SendOption.Reliable);
            writer.Write((ushort)funcao);
            writer.Write(true);
            AmongUsClient.Instance.FinishRpcImmediately(writer);

            // Não presumimos que iniciar CoSetRole significa que o jogo
            // já substituiu a função. A confirmação ocorre após a coroutine.
            jogador.StartCoroutine(VerificarAplicacaoDaFuncao(jogador, funcao, "global"));
            LogInfo($"RpcSetRoleGlobal: PlayerId={jogador.PlayerId}, Role={funcao}, canOverrideRole=true.");
        }
        catch (Exception ex)
        {
            LogErro($"RpcSetRoleGlobal falhou para PlayerId={jogador.PlayerId}: {ex}");
            throw;
        }
    }

    /// <summary>
    /// Aguarda a coroutine vanilla e confere a função real em Data.Role.
    /// O registro interno só é alterado quando a função solicitada aparece
    /// efetivamente no estado do jogador local.
    /// </summary>
    private static System.Collections.IEnumerator VerificarAplicacaoDaFuncao(
        PlayerControl jogador, RoleTypes funcaoSolicitada, string origem)
    {
        float[] esperas = { 0.5f, 1.0f, 1.5f };
        foreach (float espera in esperas)
        {
            yield return new WaitForSecondsRealtime(espera);

            if (jogador == null || jogador.Data == null || jogador.Data.Disconnected)
                yield break;

            RoleTypes? funcaoReal = null;
            try
            {
                if (jogador.Data.Role != null)
                    funcaoReal = jogador.Data.Role.Role;
            }
            catch (Exception ex)
            {
                LogAviso($"Verificação de função falhou ({origem}) para PlayerId={jogador.PlayerId}: {ex.Message}");
            }

            if (funcaoReal.HasValue && funcaoReal.Value == funcaoSolicitada)
            {
                AtualizarRegistroLocal(jogador, funcaoSolicitada);
                LogInfo($"Confirmação de troca ({origem}): PlayerId={jogador.PlayerId}, função real={funcaoReal.Value}.");
                yield break;
            }
        }

        if (jogador != null && jogador.Data != null)
        {
            string funcaoAtual;
            try { funcaoAtual = jogador.Data.Role?.Role.ToString() ?? "(Role nulo)"; }
            catch (Exception ex) { funcaoAtual = "<erro:" + ex.GetType().Name + ">"; }

            LogAviso($"Troca não confirmada ({origem}): PlayerId={jogador.PlayerId}, solicitada={funcaoSolicitada}, função real após aguardar={funcaoAtual}. O registro interno não foi forçado.");
        }
    }

    /// <summary>
    /// Equivalente ao RpcSetRoleDesync do EHR: envia SetRole bruto somente
    /// ao cliente indicado. Útil para funções diferentes por observador.
    /// </summary>
    internal static bool RpcSetRoleDesync(
        PlayerControl jogador,
        RoleTypes funcao,
        int clientId,
        bool atualizarMapa = true)
    {
        if (jogador == null || jogador.Data == null ||
            AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost ||
            clientId < 0)
            return false;

        try
        {
            PlayerControl observador = EncontrarJogadorPorClientId(clientId);
            if (atualizarMapa && observador != null)
                RoleMap[(observador.PlayerId, jogador.PlayerId)] = funcao;

            // O host não recebe um RPC enviado a si próprio: aplica diretamente,
            // exatamente como o EHR faz no caminho local.
            if (AmongUsClient.Instance.ClientId == clientId)
            {
                if (AmongUsClient.Instance.AmClient)
                {
                    jogador.SetRole(funcao, true);
                }

                jogador.StartCoroutine(VerificarAplicacaoDaFuncao(jogador, funcao, "desync-local"));
                LogInfo($"RpcSetRoleDesync local: PlayerId={jogador.PlayerId}, cliente={clientId}, Role={funcao}.");
                return true;
            }

            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                jogador.NetId,
                (byte)RpcCalls.SetRole,
                SendOption.Reliable,
                clientId);
            writer.Write((ushort)funcao);
            writer.Write(true);
            AmongUsClient.Instance.FinishRpcImmediately(writer);

            LogInfo($"RpcSetRoleDesync: PlayerId={jogador.PlayerId}, cliente={clientId}, Role={funcao}, canOverrideRole=true.");
            return true;
        }
        catch (Exception ex)
        {
            LogErro($"RpcSetRoleDesync falhou: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Inicializa a matriz de funções observadas usando o estado vanilla atual.
    /// A inicialização é tardia: ocorre na primeira troca, quando as funções
    /// já foram distribuídas pelo jogo.
    /// </summary>
    private static void GarantirRoleMapInicializado()
    {
        if (PlayerControl.AllPlayerControls == null)
            return;

        List<PlayerControl> jogadores = new();
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null || jogador.Data.Disconnected)
                continue;

            jogadores.Add(jogador);
        }

        HashSet<byte> idsAtuais = jogadores.Select(p => p.PlayerId).ToHashSet();

        if (!mapaInicializado)
        {
            RoleMap.Clear();
            JogadoresDoMapa.Clear();

            foreach (PlayerControl jogador in jogadores)
                JogadoresDoMapa.Add(jogador.PlayerId);

            foreach (PlayerControl seer in jogadores)
            {
                foreach (PlayerControl target in jogadores)
                    RoleMap[(seer.PlayerId, target.PlayerId)] = target.Data.RoleType;
            }

            mapaInicializado = true;
            LogInfo($"RoleMap inicializado: {jogadores.Count} jogadores, {RoleMap.Count} relações observador/alvo.");
            return;
        }

        // Não apaga visões desync já configuradas quando alguém entra ou sai.
        // Apenas remove referências inválidas e cria as combinações que faltam.
        foreach (var chave in RoleMap.Keys
                     .Where(k => !idsAtuais.Contains(k.SeerId) ||
                                 !idsAtuais.Contains(k.TargetId))
                     .ToList())
            RoleMap.Remove(chave);

        JogadoresDoMapa.Clear();
        foreach (byte id in idsAtuais)
            JogadoresDoMapa.Add(id);

        foreach (PlayerControl seer in jogadores)
        {
            foreach (PlayerControl target in jogadores)
            {
                var chave = (seer.PlayerId, target.PlayerId);
                if (!RoleMap.ContainsKey(chave))
                    RoleMap[chave] = target.Data.RoleType;
            }
        }
    }

    /// <summary>
    /// Atualiza todas as visões de um alvo. Usado quando a operação deve ser
    /// global, como a conversão do Oportunista.
    /// </summary>
    private static void AtualizarRoleMapCompleto(PlayerControl target, RoleTypes role)
    {
        if (target == null || !mapaInicializado)
            return;

        foreach (byte seerId in JogadoresDoMapa)
            RoleMap[(seerId, target.PlayerId)] = role;
    }

    /// <summary>
    /// Recriação genérica do RpcChangeRoleBasis do EHR.
    ///
    /// Cada chave do dicionário opcional é o PlayerId do observador (seer);
    /// o valor é a RoleType que somente aquele observador deve receber para
    /// o jogador-alvo. Sem um mapa específico, a troca é global.
    ///
    /// A distribuição escolhe a RoleType mais frequente para o RPC global e,
    /// após 0,2s, envia RPCs individuais para as visões diferentes. Se não
    /// houver uma função repetida, envia tudo individualmente.
    /// </summary>
    internal static bool RpcChangeRoleBasis(
        PlayerControl target,
        RoleTypes novaFuncao,
        IReadOnlyDictionary<byte, RoleTypes> funcoesPorObservador = null)
    {
        if (target == null || target.Data == null ||
            target.Data.Disconnected ||
            AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost)
            return false;

        // O EHR evita alterar a função enquanto a tela de ejeção está ativa.
        // Reproduzimos esse cuidado; após ela fechar, aguardamos mais 1 segundo.
        if (ExileController.Instance != null)
        {
            Dictionary<byte, RoleTypes> mapaAdiado = funcoesPorObservador == null
                ? null
                : new Dictionary<byte, RoleTypes>(funcoesPorObservador);

            MonoBehaviourExtensions.StartCoroutine(
                target,
                AdiarTrocaAteFimDaEjecao(target, novaFuncao, mapaAdiado));

            LogInfo($"RpcChangeRoleBasis adiado: a tela de ejeção ainda está ativa para PlayerId={target.PlayerId}.");
            return true;
        }

        try
        {
            GarantirRoleMapInicializado();

            List<PlayerControl> jogadores = ObterJogadoresDoMapa();
            List<RoleCall> chamadas = new();

            foreach (PlayerControl seer in jogadores)
            {
                int clientId = seer.OwnerId;
                if (clientId < 0)
                    continue;

                RoleTypes roleObservada = novaFuncao;
                if (funcoesPorObservador != null &&
                    funcoesPorObservador.TryGetValue(seer.PlayerId, out RoleTypes roleEspecifica))
                    roleObservada = roleEspecifica;

                RoleMap[(seer.PlayerId, target.PlayerId)] = roleObservada;
                chamadas.Add(new RoleCall(target, roleObservada, clientId));
            }

            if (chamadas.Count == 0)
            {
                LogAviso($"RpcChangeRoleBasis não encontrou observadores válidos para PlayerId={target.PlayerId}.");
                return false;
            }

            Dictionary<RoleTypes, int> contagens = new();
            RoleTypes funcaoGlobal = default;
            int maiorContagem = 0;

            foreach (RoleCall chamada in chamadas)
            {
                int quantidade = contagens.TryGetValue(chamada.Role, out int atual)
                    ? atual + 1
                    : 1;

                contagens[chamada.Role] = quantidade;

                if (quantidade > maiorContagem)
                {
                    maiorContagem = quantidade;
                    funcaoGlobal = chamada.Role;
                }
            }

            if (maiorContagem > 1 || chamadas.Count == 1)
            {
                // Se todos concordam, inclusive quando há apenas um observador,
                // usa o fluxo global do EHR. Com um único jogador, não existe
                // motivo para cair no caminho desync-local sem RPC global.
                // A maioria recebe uma única transmissão global.
                RpcSetRoleGlobal(target, funcaoGlobal);

                List<RoleCall> correcoes = chamadas
                    .Where(x => x.Role != funcaoGlobal)
                    .ToList();

                if (correcoes.Count > 0)
                {
                    target.StartCoroutine(
                        EnviarCorrecoesAposRoleGlobal(correcoes, funcaoGlobal));
                }
            }
            else
            {
                // Sem maioria, não existe vantagem em enviar um RPC global.
                foreach (RoleCall chamada in chamadas)
                    RpcSetRoleDesync(target, chamada.Role, chamada.ClientId);
            }

            LogInfo(
                $"RpcChangeRoleBasis concluído: PlayerId={target.PlayerId}, " +
                $"observadores={chamadas.Count}, função global={(maiorContagem > 1 ? funcaoGlobal.ToString() : "nenhuma")}, " +
                $"correções individuais={chamadas.Count(x => x.Role != funcaoGlobal)}.");

            return true;
        }
        catch (Exception ex)
        {
            LogErro($"RpcChangeRoleBasis falhou para PlayerId={target.PlayerId}: {ex}");
            return false;
        }
    }

    private static System.Collections.IEnumerator AdiarTrocaAteFimDaEjecao(
        PlayerControl target,
        RoleTypes novaFuncao,
        IReadOnlyDictionary<byte, RoleTypes> funcoesPorObservador)
    {
        while (ExileController.Instance != null)
            yield return null;

        yield return new WaitForSecondsRealtime(1f);

        if (target == null || target.Data == null || target.Data.Disconnected ||
            AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost)
            yield break;

        RpcChangeRoleBasis(target, novaFuncao, funcoesPorObservador);
    }

    private static void AplicarFuncaoComRoleMap(PlayerControl target, RoleTypes novaFuncao)
    {
        if (target == null || target.Data == null)
            return;

        if (!RpcChangeRoleBasis(target, novaFuncao))
            throw new InvalidOperationException(
                $"Não foi possível distribuir a função {novaFuncao} para PlayerId={target.PlayerId}.");
    }

    private static System.Collections.IEnumerator EnviarCorrecoesAposRoleGlobal(
        List<RoleCall> correcoes, RoleTypes funcaoGlobal)
    {
        yield return new WaitForSecondsRealtime(0.2f);

        foreach (RoleCall correcao in correcoes)
        {
            if (correcao == null || correcao.Target == null ||
                correcao.Role == funcaoGlobal)
                continue;

            RpcSetRoleDesync(
                correcao.Target, correcao.Role, correcao.ClientId);
        }
    }

    private static List<PlayerControl> ObterJogadoresDoMapa()
    {
        List<PlayerControl> resultado = new();
        if (PlayerControl.AllPlayerControls == null)
            return resultado;

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador != null && jogador.Data != null &&
                !jogador.Data.Disconnected && JogadoresDoMapa.Contains(jogador.PlayerId))
                resultado.Add(jogador);
        }

        return resultado;
    }

    private static PlayerControl EncontrarJogadorPorClientId(int clientId)
    {
        foreach (PlayerControl jogador in ObterJogadoresDoMapa())
            if (jogador.OwnerId == clientId)
                return jogador;
        return null;
    }

    private static void AtualizarRegistroLocal(
        PlayerControl jogador, RoleTypes? funcaoForcada = null)
    {
        if (jogador == null || jogador.Data == null)
            return;

        RoleTypes funcaoLocal = funcaoForcada ??
            (jogador.Data.Role != null ? jogador.Data.Role.Role : jogador.Data.RoleType);

        PlayerControl local = PlayerControl.LocalPlayer;
        if (!funcaoForcada.HasValue && local != null &&
            RoleMap.TryGetValue((local.PlayerId, jogador.PlayerId), out RoleTypes roleMapLocal))
            funcaoLocal = roleMapLocal;

        RegistroFuncoesJogadores.Atribuicao atual =
            RegistroFuncoesJogadores.Obter(jogador.PlayerId);
        RegistroFuncoesJogadores.Registrar(
            jogador, funcaoLocal, atual?.FuncaoPersonalizada);
    }

    internal static void LimparMapa()
    {
        RoleMap.Clear();
        JogadoresDoMapa.Clear();
        mapaInicializado = false;
        LogInfo("RoleMap limpo ao encerrar a partida.");
    }

    private static void AtualizarHudDaFuncaoLocal(List<TrocaPendente> trocas)
    {
        PlayerControl local = PlayerControl.LocalPlayer;
        if (local == null || HudManager.Instance?.ReportButton == null)
            return;

        TrocaPendente propria = trocas.FirstOrDefault(x =>
            x.Jogador != null && x.Jogador.PlayerId == local.PlayerId);

        if (propria == null)
            return;

        try
        {
            HudManager.Instance.ReportButton.gameObject.SetActive(
                !RoleManager.IsGhostRole(propria.Funcao));
        }
        catch (Exception ex)
        {
            LogAviso("Não foi possível atualizar o botão de reporte após a troca: " + ex.Message);
        }
    }

    private static void LogInfo(string mensagem) =>
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("[RoleSwitchAPI] " + mensagem);

    private static void LogAviso(string mensagem) =>
        YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("[RoleSwitchAPI] " + mensagem);

    private static void LogErro(string mensagem) =>
        YouTubeVoteBridgePlugin.Instance?.Log.LogError("[RoleSwitchAPI] " + mensagem);
}

/// <summary>
/// Diagnóstico passivo de RpcSetRole. Não intercepta nem altera o sorteio vanilla.
/// </summary>
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RpcSetRole))]
internal static class RoleSwitchRpcSetRolePatch
{
    private static void Postfix(PlayerControl __instance, RoleTypes roleType, bool canOverrideRole)
    {
        string roleAtual = "(Data.Role nulo)";
        try { roleAtual = __instance?.Data?.Role?.Role.ToString() ?? "(Role nulo)"; }
        catch (Exception ex) { roleAtual = "<erro:" + ex.GetType().Name + ">"; }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[RoleRevealDiag] RpcSetRole playerId={__instance?.PlayerId} roleSolicitada={roleType} roleAtual={roleAtual} canOverride={canOverrideRole}");
    }
}
