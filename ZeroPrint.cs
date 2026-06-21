using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;
using StockSharp.Messages;

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
            //  ATAS Platform é construído sobre StockSharp.
            //  Os dados de footprint (cluster) estão em:
            //    candle.VolumeProfileInfo          → CandleMessageVolumeProfile
            //    .PriceLevels                      → IList<CandlePriceLevel>
            //    CandlePriceLevel.BuyVolume        → volume de compras (ask side)
            //    CandlePriceLevel.SellVolume       → volume de vendas (bid side)
            //    CandlePriceLevel.Price            → preço do nível
            //
            //  PriceLevels contém apenas níveis COM volume (não inclui zeros),
            //  por isso os ticks ausentes da lista são os Zero Prints.
            // ════════════════════════════════════════════════════════════════

            // ⚠ VERIFICAR: se candle.VolumeProfileInfo não compilar, significa
            // que IndicatorCandle não herda directamente de CandleMessage.
            // Nesse caso, experimentar:
            //   candle.Candle?.VolumeProfileInfo
            //   (candle as CandleMessage)?.VolumeProfileInfo
            var profile = candle.VolumeProfileInfo;  // ⚠ VERIFICAR

            if (profile?.PriceLevels == null)
                return;

            // Construir conjunto de preços que têm volume registado
            var hasVolume = new HashSet<decimal>();
            foreach (var level in profile.PriceLevels)
                hasVolume.Add(level.Price);

            // Se não há dados, barra ainda sem footprint — ignorar
            if (hasVolume.Count == 0)
                return;

            // Ticks no range sem qualquer volume registado → Zero Prints
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
