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

        public ZeroPrint() : base(true)
        {
            DenyToChangePanel = true;
            EnableCustomDrawing = true;
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

            // Passar por todos os ticks do range num único ciclo:
            // - Conta níveis com volume (confirma que é um gráfico cluster)
            // - Recolhe ticks candidatos a Zero Print (sem volume)
            var candidatos = new List<decimal>();
            bool temDadosCluster = false;

            for (decimal price = low; price <= high; price += tickSize)
            {
                var pvi = candle.GetPriceVolumeInfo(price);

                if (pvi != null && (pvi.Bid > 0m || pvi.Ask > 0m))
                    temDadosCluster = true;
                else
                    candidatos.Add(price);
            }

            // Só adicionar zero prints se a vela tem dados de cluster.
            // Se GetPriceVolumeInfo devolve null para tudo, é um gráfico
            // sem footprint (vela normal) — ignorar.
            if (!temDadosCluster)
                return;

            foreach (var price in candidatos)
                _activeLines.Add((price, bar));
        }

        // ── Rendering ─────────────────────────────────────────────────────────

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (_activeLines.Count == 0)
                return;

            var pen = new RenderPen(_corLinha, _espessura);
            const int LargeRight = 100_000;

            foreach (var line in _activeLines)
            {
                int x1 = (int)ChartInfo.GetXByBar(line.OriginBar, true);
                int y  = (int)ChartInfo.GetYByPrice(line.Price, false);

                int x2 = _limitarRange
                    ? (int)ChartInfo.GetXByBar(line.OriginBar + _maxCandles, false)
                    : x1 + LargeRight;

                context.DrawLine(pen, x1, y, x2, y);
            }
        }
    }
}
