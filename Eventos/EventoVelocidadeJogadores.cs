using System;
using System.Collections.Generic;
using AmongUs.GameOptions;
using Hazel;
using UnityEngine;

namespace YouTubeVoteBridge;

// Replica a ideia do EHR: manter velocidade individual por jogador e reaplicar
// as opções enquanto o efeito estiver ativo. Para clientes remotos, envia a
// LogicOptions específica ao OwnerId; para o host local, atualiza as opções locais.
internal static class EventoVelocidadeJogadores
{
    private sealed class Efeito
    {
        public float VelocidadeOriginal;
        public float VelocidadeFisicaOriginal;
        public float VelocidadeFantasmaFisicaOriginal;
        public bool TemVelocidadeFisicaOriginal;
        public float Multiplicador;
        public float ExpiraEm;
        public float ProximoEnvio;
    }

    private static readonly Dictionary<byte, Efeito> Efeitos = new();
    private static float? VelocidadeBase;

    private const float IntervaloReenvio = 0.75f;
    private static float ProximoDiagnosticoComponentes;

    public static void AplicarMultiplicador(byte playerId, float multiplicador, float duracao)
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        PlayerControl jogador = EncontrarJogador(playerId);
        if (jogador == null || jogador.Data == null)
            return;

        if (!ObterVelocidadeBase(out float original))
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Não foi possível ler a velocidade original das opções.");
            return;
        }

        if (!Efeitos.TryGetValue(playerId, out Efeito efeito))
        {
            efeito = new Efeito { VelocidadeOriginal = original };
            Efeitos[playerId] = efeito;
        }

        if (jogador.AmOwner && jogador.MyPhysics != null && !efeito.TemVelocidadeFisicaOriginal)
        {
            efeito.VelocidadeFisicaOriginal = jogador.MyPhysics.Speed;
            efeito.VelocidadeFantasmaFisicaOriginal = jogador.MyPhysics.GhostSpeed;
            efeito.TemVelocidadeFisicaOriginal = true;
        }

        efeito.Multiplicador = multiplicador;
        efeito.ExpiraEm = Time.realtimeSinceStartup + duracao;
        efeito.ProximoEnvio = 0f;

        float velocidadeSolicitada = multiplicador < 0f
            ? original * multiplicador
            : Mathf.Clamp(original * multiplicador, 0.0001f, 3f);
        bool aplicadoOuEnfileirado = AplicarAoJogador(jogador, efeito);

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"Velocidade individual: jogador={jogador.Data.PlayerName}, " +
            $"PlayerId={playerId}, OwnerId={jogador.OwnerId}, " +
            $"base={original:0.####}, alvo={velocidadeSolicitada:0.####}, " +
            $"multiplicador={multiplicador:0.####}x, duração={duracao:0}s, " +
            $"caminho={(jogador.AmOwner ? "local" : "remoto")}, " +
            $"resultado={(aplicadoOuEnfileirado ? "enviado/aplicado" : "FALHOU")}.");
    }

    public static void Limpar(byte playerId)
    {
        if (!Efeitos.TryGetValue(playerId, out Efeito efeito))
            return;

        PlayerControl jogador = EncontrarJogador(playerId);
        if (jogador != null && jogador.Data != null && YouTubeVoteBridgePlugin.EhHost())
        {
            if (jogador.AmOwner && jogador.MyPhysics != null && efeito.TemVelocidadeFisicaOriginal)
            {
                // Não altere CurrentGameOptions para congelar/acelerar o host:
                // essas opções são globais e podem afetar os demais jogadores.
                jogador.MyPhysics.Speed = efeito.VelocidadeFisicaOriginal;
                jogador.MyPhysics.GhostSpeed = efeito.VelocidadeFantasmaFisicaOriginal;
            }
            else if (jogador.AmOwner)
            {
                DefinirVelocidadeLocal(efeito.VelocidadeOriginal);
            }
            else
            {
                EnviarOpcoesBrutas(jogador, efeito.VelocidadeOriginal);
            }
        }

        Efeitos.Remove(playerId);
        if (Efeitos.Count == 0)
            VelocidadeBase = null;

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"Velocidade restaurada para PlayerId={playerId}.");
    }

    public static void Atualizar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || Efeitos.Count == 0)
            return;

        float agora = Time.realtimeSinceStartup;
        List<byte> expirados = new();

        foreach (KeyValuePair<byte, Efeito> item in Efeitos)
        {
            if (agora >= item.Value.ExpiraEm)
                expirados.Add(item.Key);
        }

        foreach (byte id in expirados)
            Limpar(id);

        if (Efeitos.Count == 0)
            return;

        foreach (KeyValuePair<byte, Efeito> item in Efeitos)
        {
            if (agora < item.Value.ProximoEnvio)
                continue;

            PlayerControl jogador = EncontrarJogador(item.Key);
            if (jogador == null || jogador.Data == null ||
                jogador.Data.Disconnected || jogador.Data.IsDead)
            {
                expirados.Add(item.Key);
                continue;
            }

            AplicarAoJogador(jogador, item.Value);
            item.Value.ProximoEnvio = agora + IntervaloReenvio;
        }

        foreach (byte id in expirados)
            Limpar(id);
    }

    private static bool AplicarAoJogador(PlayerControl jogador, Efeito efeito)
    {
        if (jogador.AmOwner)
        {
            // O caminho local altera apenas a física deste jogador. Modificar
            // CurrentGameOptions aqui afeta a partida inteira, não só o host.
            if (jogador.MyPhysics == null || !efeito.TemVelocidadeFisicaOriginal)
                return false;

            jogador.MyPhysics.Speed =
                efeito.VelocidadeFisicaOriginal * efeito.Multiplicador;
            jogador.MyPhysics.GhostSpeed =
                efeito.VelocidadeFantasmaFisicaOriginal * efeito.Multiplicador;
            return true;
        }

        float velocidade = efeito.VelocidadeOriginal * efeito.Multiplicador;
        velocidade = velocidade < 0f
            ? -Mathf.Clamp(Mathf.Abs(velocidade), 0.0001f, 3f)
            : Mathf.Clamp(velocidade, 0.0001f, 3f);

        return EnviarOpcoesBrutas(jogador, velocidade);
    }

    private static bool ObterVelocidadeBase(out float velocidade)
    {
        velocidade = 1f;
        if (VelocidadeBase.HasValue)
        {
            velocidade = VelocidadeBase.Value;
            return true;
        }

        try
        {
            var opcoes = GameOptionsManager.Instance?.CurrentGameOptions;
            if (opcoes == null)
                return false;

            velocidade = opcoes.GetFloat(FloatOptionNames.PlayerSpeedMod);
            VelocidadeBase = velocidade;
            return true;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Falha ao ler PlayerSpeedMod: " + ex.Message);
            return false;
        }
    }

    private static void DefinirVelocidadeLocal(float velocidade)
    {
        try
        {
            var opcoes = GameOptionsManager.Instance?.CurrentGameOptions;
            if (opcoes == null)
                return;

            opcoes.SetFloat(
                FloatOptionNames.PlayerSpeedMod,
                (velocidade < 0f ? -Mathf.Clamp(Mathf.Abs(velocidade), 0.0001f, 3f) : Mathf.Clamp(velocidade, 0.0001f, 3f)));

            // O EHR não altera apenas CurrentGameOptions: ele também entrega
            // as opções ao componente LogicOptions para que o jogo as aplique.
            if (GameManager.Instance?.LogicComponents != null)
            {
                foreach (GameLogicComponent componente in GameManager.Instance.LogicComponents)
                {
                    if (componente is LogicOptions logicOptions)
                        logicOptions.SetGameOptions(opcoes);
                }
            }

            GameOptionsManager.Instance.CurrentGameOptions = opcoes;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "Falha ao aplicar velocidade local: " + ex);
        }
    }

    private static PlayerControl EncontrarJogador(byte playerId)
    {
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            if (jogador != null && jogador.PlayerId == playerId)
                return jogador;
        return null;
    }

    private static bool EnviarOpcoesBrutas(PlayerControl jogador, float velocidade)
    {
        if (jogador == null || AmongUsClient.Instance == null ||
            GameManager.Instance == null || !YouTubeVoteBridgePlugin.EhHost())
            return false;

        var opcoes = GameOptionsManager.Instance?.CurrentGameOptions;
        if (opcoes == null)
            return false;

        NormalGameOptionsV12 opcoesNormais;
        try
        {
            opcoesNormais = opcoes.TryCast<NormalGameOptionsV12>();
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Não foi possível converter as opções para NormalGameOptionsV12: " + ex.Message);
            return false;
        }

        if (opcoesNormais == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Envio de velocidade cancelado: opções atuais não são NormalGameOptionsV12.");
            return false;
        }

        byte[] dadosOpcoes;
        try
        {
            float velocidadeAnterior = opcoesNormais.GetFloat(FloatOptionNames.PlayerSpeedMod);
            float velocidadeSerializada = velocidade < 0f
                ? -Mathf.Clamp(Mathf.Abs(velocidade), 0.0001f, 3f)
                : Mathf.Clamp(velocidade, 0.0001f, 3f);
            opcoesNormais.SetFloat(
                FloatOptionNames.PlayerSpeedMod,
                velocidadeSerializada);

            MessageWriter serializador = MessageWriter.Get();
            try
            {
                serializador.Write(opcoesNormais.Version);
                serializador.StartMessage(0);
                serializador.Write((byte)opcoesNormais.GameMode);
                NormalGameOptionsV12.Serialize(serializador, opcoesNormais);
                serializador.EndMessage();
                dadosOpcoes = serializador.ToByteArray(false);
            }
            finally
            {
                serializador.Recycle();
                opcoesNormais.SetFloat(FloatOptionNames.PlayerSpeedMod, velocidadeAnterior);
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "Falha ao serializar opções de velocidade: " + ex);
            return false;
        }

        bool encontrouLogica = false;
        bool enviou = false;
        try
        {
            int quantidadeLogica = GameManager.Instance.LogicComponents.Count;
            for (int i = 0; i < quantidadeLogica; i++)
            {
                if (GameManager.Instance.LogicComponents[i] is not LogicOptions)
                    continue;

                encontrouLogica = true;
                MessageWriter pacote = MessageWriter.Get(SendOption.Reliable);
                try
                {
                    // Mesmo envelope usado pelo PlayerGameOptionsSender do EHR.
                    pacote.StartMessage(6);
                    pacote.Write(AmongUsClient.Instance.GameId);
                    pacote.WritePacked(jogador.OwnerId);

                    pacote.StartMessage(1);
                    pacote.WritePacked(GameManager.Instance.NetId);
                    pacote.StartMessage((byte)i);
                    pacote.WriteBytesAndSize(dadosOpcoes);
                    pacote.EndMessage();
                    pacote.EndMessage();
                    pacote.EndMessage();

                    YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                        $"[Velocidade] Enviando opções: PlayerId={jogador.PlayerId}, " +
                        $"OwnerId={jogador.OwnerId}, LogicOptionsIndex={i}, " +
                        $"velocidade={velocidade:0.####}, bytesOpções={dadosOpcoes.Length}, " +
                        $"bytesPacote={pacote.Length}.");

                    AmongUsClient.Instance.SendOrDisconnect(pacote);
                    enviou = true;

                    YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                        $"[Velocidade] SendOrDisconnect retornou sem exceção: " +
                        $"PlayerId={jogador.PlayerId}, OwnerId={jogador.OwnerId}, " +
                        $"LogicOptionsIndex={i}. Isso confirma chamada local, não recebimento pelo cliente.");
                }
                finally
                {
                    pacote.Recycle();
                }
            }

            if (!encontrouLogica && Time.realtimeSinceStartup >= ProximoDiagnosticoComponentes)
            {
                ProximoDiagnosticoComponentes = Time.realtimeSinceStartup + 5f;

                string componentes = "indisponível";
                string referenciaDireta = "não consultada";
                string indiceReferenciaDireta = "não disponível";
                int quantidade = -1;

                try
                {
                    GameManager gerenciador = GameManager.Instance;
                    var lista = gerenciador?.LogicComponents;

                    // Diagnóstico: comparar a propriedade direta LogicOptions com a lista
                    // usada pelo EHR para descobrir o índice de rede correto.
                    LogicOptions logicaDireta = gerenciador?.LogicOptions;
                    if (logicaDireta == null)
                    {
                        referenciaDireta = "null";
                    }
                    else
                    {
                        referenciaDireta =
                            $"{logicaDireta.GetType().FullName}; Assembly={logicaDireta.GetType().Assembly.GetName().Name}";
                    }

                    if (lista != null)
                    {
                        quantidade = lista.Count;
                        var nomes = new List<string>();
                        for (int i = 0; i < lista.Count; i++)
                        {
                            var componente = lista[i];
                            if (componente == null)
                            {
                                nomes.Add($"{i}:null");
                                continue;
                            }

                            string tipo = componente.GetType().FullName;
                            string assembly = componente.GetType().Assembly.GetName().Name;
                            bool mesmaReferencia = logicaDireta != null &&
                                ReferenceEquals(componente, logicaDireta);

                            nomes.Add($"{i}:{tipo}; Assembly={assembly}; " +
                                $"LogicOptionsDireto={mesmaReferencia}");

                            if (mesmaReferencia)
                                indiceReferenciaDireta = i.ToString();
                        }

                        componentes = nomes.Count == 0
                            ? "(lista vazia)"
                            : string.Join(" || ", nomes);
                    }
                }
                catch (Exception ex)
                {
                    componentes = "erro ao inspecionar: " + ex;
                }

                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    $"[Velocidade][DIAGNOSTICO] Nenhum LogicOptions na lista; pacote não enviado. " +
                    $"PlayerId={jogador.PlayerId}, OwnerId={jogador.OwnerId}, " +
                    $"GameManagerNetId={GameManager.Instance?.NetId}, " +
                    $"quantidadeLogicComponents={quantidade}, " +
                    $"LogicOptionsDireto=[{referenciaDireta}], " +
                    $"indiceLogicOptionsDireto={indiceReferenciaDireta}, " +
                    $"componentes=[{componentes}].");
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                $"[Velocidade] Falha ao enviar opções individuais: " +
                $"PlayerId={jogador.PlayerId}, OwnerId={jogador.OwnerId}, velocidade={velocidade:0.####}. " + ex);
            return false;
        }

        return enviou;
    }
}

internal static class EventoVelocidadeTemporaria
{
    private const float DuracaoSegundos = 15f;

    public static void Supervelocidade() => AplicarATodos(2.5f);
    public static void VelocidadeReduzida() => AplicarATodos(0.35f);

    private static void AplicarATodos(float multiplicador)
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        int afetados = 0;
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null ||
                jogador.Data.IsDead || jogador.Data.Disconnected)
                continue;

            EventoVelocidadeJogadores.AplicarMultiplicador(
                jogador.PlayerId, multiplicador, DuracaoSegundos);
            afetados++;
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"Evento de velocidade individual: multiplicador={multiplicador:0.####}x, " +
            $"jogadores={afetados}, duração={DuracaoSegundos:0}s.");
    }
}
