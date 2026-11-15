using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using UnityEngine;

namespace YouTubeVoteBridge;

/// <summary>
/// Ferramentas do controlador F8 para registrar a geometria dos mapas e
/// visualizar o trajeto percorrido pelo jogador local.
/// </summary>
internal static class ExploracaoMapaF8
{
    private const float IntervaloRegistro = 0.15f;
    private const float DistanciaMinimaPonto = 0.08f;
    private const int LimitePontos = 3000;

    [Serializable]
    private sealed class ArquivoMapa
    {
        public string mapa;
        public int mapId;
        public string capturadoEmUtc;
        public int quantidadeColliders;
        public int quantidadeSalas;
        public List<DadoSala> salas = new();
        public List<DadoCollider> colliders = new();
    }

    [Serializable]
    private sealed class DadoSala
    {
        public string nome;
        public string roomId;
        public float minX;
        public float minY;
        public float maxX;
        public float maxY;
    }

    [Serializable]
    private sealed class DadoCollider
    {
        public string objeto;
        public string hierarquia;
        public string tipo;
        public bool habilitado;
        public bool trigger;
        public float minX;
        public float minY;
        public float maxX;
        public float maxY;
        public float centroX;
        public float centroY;
        public float largura;
        public float altura;
        public float rotacaoZ;
    }

    private static readonly List<Vector3> pontosExplorados = new();
    private static LineRenderer linhaExploracao;
    private static Material materialExploracao;
    private static bool exploracaoAtiva;
    private static float proximoRegistro;

    internal static bool ExploracaoAtiva => exploracaoAtiva;

    internal static bool AlternarExploracao()
    {
        exploracaoAtiva = !exploracaoAtiva;
        proximoRegistro = 0f;

        if (exploracaoAtiva)
        {
            pontosExplorados.Clear();
            GarantirLinha();
            if (linhaExploracao != null)
            {
                linhaExploracao.positionCount = 0;
                linhaExploracao.enabled = true;
            }
        }
        else if (linhaExploracao != null)
        {
            linhaExploracao.enabled = false;
        }

        return exploracaoAtiva;
    }

    internal static void Atualizar()
    {
        if (!exploracaoAtiva || PlayerControl.LocalPlayer == null ||
            ShipStatus.Instance == null)
            return;

        float agora = Time.realtimeSinceStartup;
        if (agora < proximoRegistro)
            return;
        proximoRegistro = agora + IntervaloRegistro;

        Vector3 posicao = PlayerControl.LocalPlayer.transform.position;
        if (pontosExplorados.Count > 0 &&
            Vector3.Distance(pontosExplorados[pontosExplorados.Count - 1], posicao) <
            DistanciaMinimaPonto)
            return;

        pontosExplorados.Add(posicao);
        if (pontosExplorados.Count > LimitePontos)
            pontosExplorados.RemoveAt(0);

        GarantirLinha();
        if (linhaExploracao == null)
            return;

        linhaExploracao.positionCount = pontosExplorados.Count;
        linhaExploracao.SetPositions(pontosExplorados.ToArray());
        linhaExploracao.enabled = true;
    }

    internal static void Resetar()
    {
        exploracaoAtiva = false;
        proximoRegistro = 0f;
        pontosExplorados.Clear();

        if (linhaExploracao != null)
        {
            UnityEngine.Object.Destroy(linhaExploracao.gameObject);
            linhaExploracao = null;
        }

        if (materialExploracao != null)
        {
            UnityEngine.Object.Destroy(materialExploracao);
            materialExploracao = null;
        }
    }

    internal static bool MapearMapaAtual(out string resultado)
    {
        resultado = "Não foi possível mapear o mapa atual.";

        if (ShipStatus.Instance == null ||
            GameOptionsManager.Instance?.CurrentGameOptions == null)
        {
            resultado = "O mapa ainda não está carregado.";
            return false;
        }

        int mapId = GameOptionsManager.Instance.CurrentGameOptions.MapId;
        string nomeMapa = NomeMapa(mapId);
        if (nomeMapa == null)
        {
            resultado = "Mapa não suportado para mapeamento (Dleks não gera arquivo próprio).";
            return false;
        }

        try
        {
            ArquivoMapa dados = new()
            {
                mapa = nomeMapa,
                mapId = mapId,
                capturadoEmUtc = DateTime.UtcNow.ToString("o")
            };

            foreach (var sala in ShipStatus.Instance.AllRooms)
            {
                if (sala == null || sala.roomArea == null)
                    continue;

                Bounds b = sala.roomArea.bounds;
                dados.salas.Add(new DadoSala
                {
                    nome = sala.name,
                    roomId = sala.RoomId.ToString(),
                    minX = b.min.x,
                    minY = b.min.y,
                    maxX = b.max.x,
                    maxY = b.max.y
                });
            }

            Collider2D[] colliders =
                ShipStatus.Instance.GetComponentsInChildren<Collider2D>(true);

            foreach (Collider2D collider in colliders)
            {
                if (collider == null)
                    continue;

                Bounds b = collider.bounds;
                Transform t = collider.transform;
                dados.colliders.Add(new DadoCollider
                {
                    objeto = collider.name,
                    hierarquia = ObterHierarquia(t),
                    tipo = collider.GetType().Name,
                    habilitado = collider.enabled && collider.gameObject.activeInHierarchy,
                    trigger = collider.isTrigger,
                    minX = b.min.x,
                    minY = b.min.y,
                    maxX = b.max.x,
                    maxY = b.max.y,
                    centroX = b.center.x,
                    centroY = b.center.y,
                    largura = b.size.x,
                    altura = b.size.y,
                    rotacaoZ = t.eulerAngles.z
                });
            }

            dados.quantidadeColliders = dados.colliders.Count;
            dados.quantidadeSalas = dados.salas.Count;

            string pasta = Path.Combine(
                BepInEx.Paths.ConfigPath, "YouTubeVoteBridge", "Mapas");
            Directory.CreateDirectory(pasta);

            string arquivo = Path.Combine(pasta, nomeMapa + ".json");
            File.WriteAllText(arquivo, JsonSerializer.Serialize(dados, new JsonSerializerOptions
            {
                WriteIndented = true,
                IncludeFields = true
            }));

            resultado = "Mapa " + nomeMapa + " mapeado: " +
                dados.quantidadeColliders + " colliders e " +
                dados.quantidadeSalas + " salas. Arquivo: " + arquivo;

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F8/Mapeamento] " + resultado);

            if (mapId == 4)
            {
                int eletrica = dados.colliders.Count(c =>
                    c.hierarquia.IndexOf("Electrical", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    c.objeto.IndexOf("Electrical", StringComparison.OrdinalIgnoreCase) >= 0);

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F8/Mapeamento] Airship: " + eletrica +
                    " colliders associados à área Electrical foram incluídos no snapshot.");
            }

            return true;
        }
        catch (Exception ex)
        {
            resultado = "Falha ao mapear " + nomeMapa + ": " + ex.Message;
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[F8/Mapeamento] " + ex);
            return false;
        }
    }

    private static string NomeMapa(int mapId)
    {
        return mapId switch
        {
            0 => "Skeld",
            1 => "MiraHQ",
            2 => "Polus",
            4 => "Airship",
            5 => "TheFungle",
            _ => null
        };
    }

    private static string ObterHierarquia(Transform transformacao)
    {
        if (transformacao == null)
            return "";

        List<string> partes = new();
        Transform atual = transformacao;
        while (atual != null && partes.Count < 32)
        {
            partes.Add(atual.name);
            if (ShipStatus.Instance != null &&
                atual == ShipStatus.Instance.transform)
                break;
            atual = atual.parent;
        }

        partes.Reverse();
        return string.Join("/", partes);
    }

    private static void GarantirLinha()
    {
        if (linhaExploracao != null)
            return;

        GameObject objeto = new("YouTubeVoteBridge_F8_ExploracaoJogador");
        UnityEngine.Object.DontDestroyOnLoad(objeto);

        linhaExploracao = objeto.AddComponent<LineRenderer>();
        linhaExploracao.useWorldSpace = true;
        linhaExploracao.loop = false;
        linhaExploracao.positionCount = 0;
        linhaExploracao.startWidth = 0.055f;
        linhaExploracao.endWidth = 0.055f;
        linhaExploracao.numCapVertices = 2;
        linhaExploracao.numCornerVertices = 2;
        linhaExploracao.startColor = new Color(0.1f, 0.9f, 1f, 0.95f);
        linhaExploracao.endColor = new Color(0.1f, 0.9f, 1f, 0.95f);

        Shader shader = Shader.Find("Sprites/Default");
        if (shader != null)
        {
            materialExploracao = new Material(shader);
            linhaExploracao.material = materialExploracao;
        }

        linhaExploracao.enabled = exploracaoAtiva;
    }
}
