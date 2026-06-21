using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

// ============================================================
//  Zero Print Pereira
//  Indicador para ATAS Platform SDK 10
//
//  Desenha linhas horizontais nos níveis do footprint onde
//  tanto o volume Bid como o volume Ask são zero (lacunas
//  no cluster).  A linha persiste até ser tocada pelo wick
//  de uma candle subsequente.
// ============================================================

namespace ZeroPrintIndicator
{
    [DisplayName("Zero Print Pereira")]
    public class ZeroPrint : Indicator
    {
        // ── Private fields ────────────────────────────────────────────────────

        private Color _corLinha     = Color.Tomato;
        private int   _espessura    = 1;
        private bool  _limitarRange = false;
        private int   _maxCandles   = 50;

        // Lista de níveis Zero Print ainda activos (não tocados)
        private readonly List<(decimal Price, int OriginBar)> _activeLines
            = new List<(decimal Price, int OriginBar)>();

        // Barras fechadas já processadas (evita re-scan desnecessário)
        private readonly HashSet<int> _scannedBars = new HashSet<int>();

        // ── Parâmetros ────────────────────────────────────────────────────────

        [Display(Name = "Cor da Linha", GroupName = "Aparência", Order = 1)]
        public Color CorLinhaZeroPrint
        {
            get => _corLinha;
            set { _corLinha = value; RecalculateValues(); }
        }

        [Display(Name = "Espessura da Linha", GroupName = "Aparência", Order = 2)]
        public int EspessuraLinha
        {
            get => _espessura;
            set { _espessura = Math.Max(1, value); RecalculateValues(); }
        }

        [Display(Name = "Limitar Range de Candles", GroupName = "Limites", Order = 1)]
        public bool LimitarRangeCandles
        {
            get => _limitarRange;
            set { _limitarRange = value; RecalculateValues(); }
        }

        [Display(Name = "Máximo de Candles", GroupName = "Limites", Order = 2)]
        public int MaxCandles
        {
            get => _maxCandles;
            set { _maxCandles = Math.Max(1, value); RecalculateValues(); }
        }

        // ── Construtor ────────────────────────────────────────────────────────

        // ⚠ VERIFICAR: o parâmetro `true` na base class pode significar
        // "isOverlay" (desenhar no painel de preço) ou "useClusters"
        // (activar acesso ao footprint), consoante a versão do SDK 10.
        // Ambos são desejáveis aqui.  Se o SDK usar outro mecanismo para
        // activar dados de cluster, ajustar em conformidade.
        public ZeroPrint() : base(true)
        {
            DenyToChangePanel = true; // força painel de preço principal
        }

        // ── Cálculo ───────────────────────────────────────────────────────────

        protected override void OnCalculate(int bar, decimal value)
        {
            // Reset completo no início de cada recalculação
            if (bar == 0)
            {
                _activeLines.Clear();
                _scannedBars.Clear();
            }

            var candle = GetCandle(bar);
            if (candle == null)
                return;

            // ─── Passo 1: Verificar toques e expiração das linhas activas ─────

            for (int i = _activeLines.Count - 1; i >= 0; i--)
            {
                var line = _activeLines[i];

                // Nunca verificar o toque na própria candle que criou o nível
                if (line.OriginBar >= bar)
                    continue;

                // Toque: o wick da candle atravessa o preço do Zero Print
                if (candle.High >= line.Price && candle.Low <= line.Price)
                {
                    _activeLines.RemoveAt(i);
                    continue;
                }

                // Expiração: excedeu o número máximo de candles
                if (_limitarRange && (bar - line.OriginBar) >= _maxCandles)
                    _activeLines.RemoveAt(i);
            }

            // ─── Passo 2: Scan do footprint desta barra ───────────────────────

            bool isCurrent = (bar >= CurrentBar);

            // Barras fechadas: processar apenas uma vez
            if (!isCurrent && _scannedBars.Contains(bar))
                return;

            // Remover entradas antigas desta barra (necessário para re-scan
            // da barra em formação enquanto ela actualiza)
            _activeLines.RemoveAll(l => l.OriginBar == bar);

            ScanFootprint(bar, candle);

            if (!isCurrent)
                _scannedBars.Add(bar);
        }

        private void ScanFootprint(int bar, IndicatorCandle candle)
        {
            decimal tickSize = InstrumentInfo.TickSize;
            if (tickSize <= 0m)
                return;

            decimal high = candle.High;
            decimal low  = candle.Low;

            if (high < low)
                return;

            // Guarda de segurança: ignora candles com range anormal
            if ((high - low) / tickSize > 5_000m)
                return;

            // ⚠ VERIFICAR ────────────────────────────────────────────────────
            // A API de acesso ao footprint pode variar entre versões do SDK 10.
            // Estão documentadas abaixo as abordagens mais comuns.
            // Activar a que corresponder ao SDK instalado.
            //
            // ════════════════════════════════════════════════════════════════
            //  ABORDAGEM  — via colecção de price levels (activa)
            //
            //  Obtém todos os price levels do footprint e constrói um conjunto
            //  de preços COM volume (bid > 0 ou ask > 0).
            //  Depois itera tick a tick do Low ao High: qualquer tick que não
            //  esteja nesse conjunto é um Zero Print.
            //  Esta lógica é correcta tanto se a colecção devolver TODOS os
            //  ticks do range (incluindo zeros) como se devolver APENAS os
            //  ticks com volume não-zero.
            // ════════════════════════════════════════════════════════════════

            // ⚠ VERIFICAR: nome exacto da propriedade/método no SDK instalado.
            // Candidatos mais prováveis (verificar via IntelliSense ou decompiler):
            //   candle.PriceLevels
            //   candle.GetAllPriceLevels()
            //   candle.FootPrint          (se FootPrint for IEnumerable<PriceVolumeInfo>)
            //   candle.Levels
            //   candle.ClusterData
            var levels = candle.PriceLevels;  // ⚠ VERIFICAR

            if (levels == null)
                return;

            // Conjunto de preços com pelo menos um trade (bid > 0 ou ask > 0)
            var hasVolume = new HashSet<decimal>();
            foreach (var pvi in levels)
            {
                // ⚠ VERIFICAR: nomes das propriedades em PriceVolumeInfo
                // Bid → pvi.Bid  |  pvi.BidVolume  |  pvi.VolumeBid
                // Ask → pvi.Ask  |  pvi.AskVolume  |  pvi.VolumeAsk
                // Price → pvi.Price  |  pvi.Level  |  pvi.PriceLevel
                if (pvi.Bid > 0m || pvi.Ask > 0m)
                    hasVolume.Add(pvi.Price);  // ⚠ VERIFICAR: pvi.Price
            }

            // Se não há nenhum tick com volume, a barra ainda não tem dados
            // de footprint (ex: barra em formação sem trades) — não processar
            if (hasVolume.Count == 0)
                return;

            // Ticks no range sem volume → Zero Prints
            for (decimal price = low; price <= high; price += tickSize)
            {
                if (!hasVolume.Contains(price))
                    _activeLines.Add((price, bar));
            }
        }

        // ── Rendering ─────────────────────────────────────────────────────────

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (layout != DrawingLayouts.Final)
                return;

            if (_activeLines.Count == 0)
                return;

            // ⚠ VERIFICAR: construtor exacto de RenderPen no SDK instalado.
            // Possibilidades:
            //   new RenderPen(Color color, int width)
            //   new RenderPen(Color color, float width)
            var pen = new RenderPen(_corLinha, _espessura); // ⚠ VERIFICAR

            // RenderContext não expõe ClipRectangle directamente no SDK 10.
            // Usamos uma largura grande (100 000 px) para linhas ilimitadas;
            // o renderer clipa automaticamente à área visível.
            // ⚠ VERIFICAR: se o SDK expuser context.Clip, context.Bounds ou
            // ChartInfo.Region, substituir o valor abaixo pelo Right dessa área.
            const int LargeRight = 100_000;

            foreach (var line in _activeLines)
            {
                // ⚠ VERIFICAR: GetXByBar e GetYByPrice podem retornar int,
                // float ou double; ajustar o cast conforme o SDK instalado.
                int x1 = (int)ChartInfo.GetXByBar(line.OriginBar);  // ⚠ VERIFICAR
                int y  = (int)ChartInfo.GetYByPrice(line.Price);     // ⚠ VERIFICAR

                int x2 = _limitarRange
                    ? (int)ChartInfo.GetXByBar(line.OriginBar + _maxCandles) // ⚠ VERIFICAR
                    : x1 + LargeRight;

                // ⚠ VERIFICAR: assinatura de DrawLine no RenderContext:
                //   context.DrawLine(pen, x1, y, x2, y)
                //   context.DrawLine(pen, new Point(x1, y), new Point(x2, y))
                context.DrawLine(pen, x1, y, x2, y); // ⚠ VERIFICAR
            }
        }
    }
}
