﻿/* Copyright (c) 2007-2008 CSIRO
   Copyright (c) 2007-2010 Xiph.Org Foundation
   Copyright (c) 2008 Gregory Maxwell
   Originally written by Jean-Marc Valin, Gregory Maxwell, and the Opus open-source contributors
   Ported to C# by Logan Stromberg

   Redistribution and use in source and binary forms, with or without
   modification, are permitted provided that the following conditions
   are met:

   - Redistributions of source code must retain the above copyright
   notice, this list of conditions and the following disclaimer.

   - Redistributions in binary form must reproduce the above copyright
   notice, this list of conditions and the following disclaimer in the
   documentation and/or other materials provided with the distribution.

   - Neither the name of Internet Society, IETF or IETF Trust, nor the
   names of specific contributors, may be used to endorse or promote
   products derived from this software without specific prior written
   permission.

   THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
   ``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
   LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
   A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER
   OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
   EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
   PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
   PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
   LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
   NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
   SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
*/

namespace Concentus.Celt
{
    using Concentus.Celt.Enums;
    using Concentus.Celt.Structs;
    using Concentus.Common;
    using Concentus.Common.CPlusPlus;
    using Concentus.Enums;
    using System;
    using System.Diagnostics;

    internal class CeltCommon
    {
        /* Table of 6*64/x, trained on real data to minimize the average error */
        private static readonly byte[] inv_table = {
             255,255,156,110, 86, 70, 59, 51, 45, 40, 37, 33, 31, 28, 26, 25,
              23, 22, 21, 20, 19, 18, 17, 16, 16, 15, 15, 14, 13, 13, 12, 12,
              12, 12, 11, 11, 11, 10, 10, 10,  9,  9,  9,  9,  9,  9,  8,  8,
               8,  8,  8,  7,  7,  7,  7,  7,  7,  6,  6,  6,  6,  6,  6,  6,
               6,  6,  6,  6,  6,  6,  6,  6,  6,  5,  5,  5,  5,  5,  5,  5,
               5,  5,  5,  5,  5,  4,  4,  4,  4,  4,  4,  4,  4,  4,  4,  4,
               4,  4,  4,  4,  4,  4,  4,  4,  4,  4,  4,  4,  4,  4,  3,  3,
               3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  3,  2,
       };

        internal static int compute_vbr(CeltMode mode, AnalysisInfo analysis, int base_target,
              int LM, int bitrate, int lastCodedBands, int C, int intensity,
              int constrained_vbr, int stereo_saving, int tot_boost,
              int tf_estimate, int pitch_change, int maxDepth,
              OpusFramesize variable_duration, int lfe, int has_surround_mask, int surround_masking,
              int temporal_vbr)
        {
            /* The target rate in 8th bits per frame */
            int target;
            int coded_bins;
            int coded_bands;
            int tf_calibration;
            int nbEBands;
            Pointer<short> eBands;

            nbEBands = mode.nbEBands;
            eBands = mode.eBands.GetPointer();

            coded_bands = lastCodedBands != 0 ? lastCodedBands : nbEBands;
            coded_bins = eBands[coded_bands] << LM;
            if (C == 2)
                coded_bins += eBands[Inlines.IMIN(intensity, coded_bands)] << LM;

            target = base_target;
#if ENABLE_ANALYSIS
            if (analysis.valid != 0 && analysis.activity < .4)
                target -= (int)((coded_bins << EntropyCoder.BITRES) * (.4f - analysis.activity));
#endif
            /* Stereo savings */
            if (C == 2)
            {
                int coded_stereo_bands;
                int coded_stereo_dof;
                int max_frac;
                coded_stereo_bands = Inlines.IMIN(intensity, coded_bands);
                coded_stereo_dof = (eBands[coded_stereo_bands] << LM) - coded_stereo_bands;
                /* Maximum fraction of the bits we can save if the signal is mono. */
                max_frac = Inlines.DIV32_16(Inlines.MULT16_16(Inlines.QCONST16(0.8f, 15), coded_stereo_dof), coded_bins);
                stereo_saving = Inlines.MIN16(stereo_saving, Inlines.QCONST16(1.0f, 8));
                /*printf("%d %d %d ", coded_stereo_dof, coded_bins, tot_boost);*/
                target -= (int)Inlines.MIN32(Inlines.MULT16_32_Q15(max_frac, target),
                                Inlines.SHR32(Inlines.MULT16_16(stereo_saving - Inlines.QCONST16(0.1f, 8), (coded_stereo_dof << EntropyCoder.BITRES)), 8));
            }
            /* Boost the rate according to dynalloc (minus the dynalloc average for calibration). */
            target += tot_boost - (16 << LM);
            /* Apply transient boost, compensating for average boost. */
            tf_calibration = variable_duration == OpusFramesize.OPUS_FRAMESIZE_VARIABLE ?
                             Inlines.QCONST16(0.02f, 14) : Inlines.QCONST16(0.04f, 14);
            target += (int)Inlines.SHL32(Inlines.MULT16_32_Q15(tf_estimate - tf_calibration, target), 1);

#if ENABLE_ANALYSIS
            /* Apply tonality boost */
            if (analysis.valid != 0 && lfe == 0)
            {
                int tonal_target;
                float tonal;

                /* Tonality boost (compensating for the average). */
                tonal = Inlines.MAX16(0, analysis.tonality - .15f) - 0.09f;
                tonal_target = target + (int)((coded_bins << EntropyCoder.BITRES) * 1.2f * tonal);
                if (pitch_change != 0)
                    tonal_target += (int)((coded_bins << EntropyCoder.BITRES) * .8f);
                target = tonal_target;
            }
#endif

            if (has_surround_mask != 0 && lfe == 0)
            {
                int surround_target = target + (int)Inlines.SHR32(Inlines.MULT16_16(surround_masking, coded_bins << EntropyCoder.BITRES), CeltConstants.DB_SHIFT);
                /*printf("%f %d %d %d %d %d %d ", surround_masking, coded_bins, st.end, st.intensity, surround_target, target, st.bitrate);*/
                target = Inlines.IMAX(target / 4, surround_target);
            }

            {
                int floor_depth;
                int bins;
                bins = eBands[nbEBands - 2] << LM;
                /*floor_depth = Inlines.SHR32(Inlines.MULT16_16((C*bins<<EntropyCoder.BITRES),celt_log2(Inlines.SHL32(Inlines.MAX16(1,sample_max),13))), CeltConstants.DB_SHIFT);*/
                floor_depth = (int)Inlines.SHR32(Inlines.MULT16_16((C * bins << EntropyCoder.BITRES), maxDepth), CeltConstants.DB_SHIFT);
                floor_depth = Inlines.IMAX(floor_depth, target >> 2);
                target = Inlines.IMIN(target, floor_depth);
                /*printf("%f %d\n", maxDepth, floor_depth);*/
            }

            if ((has_surround_mask == 0 || lfe != 0) && (constrained_vbr != 0 || bitrate < 64000))
            {
                int rate_factor;
                rate_factor = Inlines.MAX16(0, (bitrate - 32000));
                if (constrained_vbr != 0)
                    rate_factor = Inlines.MIN16(rate_factor, Inlines.QCONST16(0.67f, 15));
                target = base_target + (int)Inlines.MULT16_32_Q15(rate_factor, target - base_target);
            }

            if (has_surround_mask == 0 && tf_estimate < Inlines.QCONST16(.2f, 14))
            {
                int amount;
                int tvbr_factor;
                amount = Inlines.MULT16_16_Q15(Inlines.QCONST16(.0000031f, 30), Inlines.IMAX(0, Inlines.IMIN(32000, 96000 - bitrate)));
                tvbr_factor = Inlines.SHR32(Inlines.MULT16_16(temporal_vbr, amount), CeltConstants.DB_SHIFT);
                target += (int)Inlines.MULT16_32_Q15(tvbr_factor, target);
            }

            /* Don't allow more than doubling the rate */
            target = Inlines.IMIN(2 * base_target, target);

            return target;
        }

        internal static int transient_analysis(int[] input, int len, int C,
                                  BoxedValue<int> tf_estimate, BoxedValue<int> tf_chan)
        {
            int i;
            int[] tmp;
            int mem0, mem1;
            int is_transient = 0;
            int mask_metric = 0;
            int c;
            int tf_max;
            int len2;

            tmp = new int[len];

            len2 = len / 2;
            for (c = 0; c < C; c++)
            {
                int mean;
                int unmask = 0;
                int norm;
                int maxE;
                mem0 = 0;
                mem1 = 0;
                /* High-pass filter: (1 - 2*z^-1 + z^-2) / (1 - z^-1 + .5*z^-2) */
                for (i = 0; i < len; i++)
                {
                    int x, y;
                    x = Inlines.SHR32(input[i + c * len], CeltConstants.SIG_SHIFT);
                    y = Inlines.ADD32(mem0, x);
                    mem0 = mem1 + y - Inlines.SHL32(x, 1);
                    mem1 = x - Inlines.SHR32(y, 1);
                    tmp[i] = Inlines.EXTRACT16(Inlines.SHR32(y, 2));
                    /*printf("%f ", tmp[i]);*/
                }
                /*printf("\n");*/
                /* First few samples are bad because we don't propagate the memory */
                Arrays.MemSet<int>(tmp, 0, 12);

                /* Normalize tmp to max range */
                {
                    int shift = 0;
                    shift = 14 - Inlines.celt_ilog2(1 + Inlines.celt_maxabs32(tmp, 0, len));
                    if (shift != 0)
                    {
                        for (i = 0; i < len; i++)
                            tmp[i] = Inlines.SHL16(tmp[i], shift);
                    }
                }

                mean = 0;
                mem0 = 0;
                /* Grouping by two to reduce complexity */
                /* Forward pass to compute the post-echo threshold*/
                for (i = 0; i < len2; i++)
                {
                    int x2 = (Inlines.PSHR32(Inlines.MULT16_16(tmp[2 * i], tmp[2 * i]) + Inlines.MULT16_16(tmp[2 * i + 1], tmp[2 * i + 1]), 16));
                    mean += x2;
                    tmp[i] = (mem0 + Inlines.PSHR32(x2 - mem0, 4));
                    mem0 = tmp[i];
                }

                mem0 = 0;
                maxE = 0;
                /* Backward pass to compute the pre-echo threshold */
                for (i = len2 - 1; i >= 0; i--)
                {
                    /* FIXME: Use PSHR16() instead */
                    tmp[i] = (mem0 + Inlines.PSHR32(tmp[i] - mem0, 3));
                    mem0 = tmp[i];
                    maxE = Inlines.MAX16(maxE, (mem0));
                }
                /*for (i=0;i<len2;i++)printf("%f ", tmp[i]/mean);printf("\n");*/

                /* Compute the ratio of the "frame energy" over the harmonic mean of the energy.
                   This essentially corresponds to a bitrate-normalized temporal noise-to-mask
                   ratio */

                /* As a compromise with the old transient detector, frame energy is the
                   geometric mean of the energy and half the max */
                /* Costs two sqrt() to avoid overflows */
                mean = Inlines.MULT16_16(Inlines.celt_sqrt(mean), Inlines.celt_sqrt(Inlines.MULT16_16(maxE, len2 >> 1)));
                /* Inverse of the mean energy in Q15+6 */
                norm = Inlines.SHL32((len2), 6 + 14) / Inlines.ADD32(CeltConstants.EPSILON, Inlines.SHR32(mean, 1));
                /* Compute harmonic mean discarding the unreliable boundaries
                   The data is smooth, so we only take 1/4th of the samples */
                unmask = 0;
                for (i = 12; i < len2 - 5; i += 4)
                {
                    int id;
                    id = Inlines.MAX32(0, Inlines.MIN32(127, Inlines.MULT16_32_Q15((tmp[i] + CeltConstants.EPSILON), norm))); /* Do not round to nearest */
                    unmask += inv_table[id];
                }
                /*printf("%d\n", unmask);*/
                /* Normalize, compensate for the 1/4th of the sample and the factor of 6 in the inverse table */
                unmask = 64 * unmask * 4 / (6 * (len2 - 17));
                if (unmask > mask_metric)
                {
                    tf_chan.Val = c;
                    mask_metric = unmask;
                }
            }
            is_transient = mask_metric > 200 ? 1 : 0;

            /* Arbitrary metric for VBR boost */
            tf_max = Inlines.MAX16(0, (Inlines.celt_sqrt(27 * mask_metric) - 42));
            /* *tf_estimate = 1 + Inlines.MIN16(1, sqrt(Inlines.MAX16(0, tf_max-30))/20); */
            tf_estimate.Val = (Inlines.celt_sqrt(Inlines.MAX32(0, Inlines.SHL32(Inlines.MULT16_16(Inlines.QCONST16(0.0069f, 14), Inlines.MIN16(163, tf_max)), 14) - Inlines.QCONST32(0.139f, 28))));
            /*printf("%d %f\n", tf_max, mask_metric);*/

#if FUZZING
            is_transient = new Random().Next() & 0x1;
#endif
            /*printf("%d %f %d\n", is_transient, (float)*tf_estimate, tf_max);*/
            return is_transient;
        }

        /* Looks for sudden increases of energy to decide whether we need to patch
           the transient decision */
        internal static int patch_transient_decision(Pointer<int> newE, Pointer<int> oldE, int nbEBands,
              int start, int end, int C)
        {
            int i, c;
            int mean_diff = 0;
            int[] spread_old = new int[26];
            /* Apply an aggressive (-6 dB/Bark) spreading function to the old frame to
               avoid false detection caused by irrelevant bands */
            if (C == 1)
            {
                spread_old[start] = oldE[start];
                for (i = start + 1; i < end; i++)
                    spread_old[i] = Inlines.MAX16((spread_old[i - 1] - Inlines.QCONST16(1.0f, CeltConstants.DB_SHIFT)), oldE[i]);
            }
            else {
                spread_old[start] = Inlines.MAX16(oldE[start], oldE[start + nbEBands]);
                for (i = start + 1; i < end; i++)
                    spread_old[i] = Inlines.MAX16((spread_old[i - 1] - Inlines.QCONST16(1.0f, CeltConstants.DB_SHIFT)),
                                          Inlines.MAX16(oldE[i], oldE[i + nbEBands]));
            }
            for (i = end - 2; i >= start; i--)
                spread_old[i] = Inlines.MAX16(spread_old[i], (spread_old[i + 1] - Inlines.QCONST16(1.0f, CeltConstants.DB_SHIFT)));
            /* Compute mean increase */
            c = 0; do
            {
                for (i = Inlines.IMAX(2, start); i < end - 1; i++)
                {
                    int x1, x2;
                    x1 = Inlines.MAX16(0, newE[i + c * nbEBands]);
                    x2 = Inlines.MAX16(0, spread_old[i]);
                    mean_diff = Inlines.ADD32(mean_diff, (Inlines.MAX16(0, Inlines.SUB16(x1, x2))));
                }
            } while (++c < C);
            mean_diff = Inlines.DIV32(mean_diff, C * (end - 1 - Inlines.IMAX(2, start)));
            /*printf("%f %f %d\n", mean_diff, max_diff, count);*/
            return (mean_diff > Inlines.QCONST16(1.0f, CeltConstants.DB_SHIFT)) ? 1 : 0;
        }

        /** Apply window and compute the MDCT for all sub-frames and
            all channels in a frame */
        internal static void compute_mdcts(CeltMode mode, int shortBlocks, Pointer<int> input,
                                  Pointer<int> output, int C, int CC, int LM, int upsample)
        {
            int overlap = mode.overlap;
            int N;
            int B;
            int shift;
            int i, b, c;
            if (shortBlocks != 0)
            {
                B = shortBlocks;
                N = mode.shortMdctSize;
                shift = mode.maxLM;
            }
            else {
                B = 1;
                N = mode.shortMdctSize << LM;
                shift = mode.maxLM - LM;
            }
            c = 0;
            do
            {
                for (b = 0; b < B; b++)
                {
                    /* Interleaving the sub-frames while doing the MDCTs */
                    MDCT.clt_mdct_forward(
                        mode.mdct,
                        input.Data, input.Offset + ((c * ((B * N) + overlap)) + (b * N)),
                        output.Data,
                        output.Offset + (b + c * N * B),
                        mode.window,
                        0,
                        overlap,
                        shift,
                        B);
                }
            } while (++c < CC);

            if (CC == 2 && C == 1)
            {
                for (i = 0; i < B * N; i++)
                {
                    output[i] = Inlines.ADD32(Inlines.HALF32(output[i]), Inlines.HALF32(output[B * N + i]));
                }
            }
            if (upsample != 1)
            {
                c = 0;
                do
                {
                    int bound = B * N / upsample;
                    for (i = 0; i < bound; i++)
                        output[c * B * N + i] *= upsample;
                    output.Point(c * B * N + bound).MemSet(0, B * N - bound);
                } while (++c < C);
            }
        }

        internal static void celt_preemphasis(Pointer<int> pcmp, Pointer<int> inp,
                                int N, int CC, int upsample, Pointer<int> coef, BoxedValue<int> mem, int clip)
        {
            int i;
            int coef0;
            int m;
            int Nu;

            coef0 = coef[0];
            m = mem.Val;

            /* Fast path for the normal 48kHz case and no clipping */
            if (coef[1] == 0 && upsample == 1 && clip == 0)
            {
                for (i = 0; i < N; i++)
                {
                    int x = pcmp[CC * i];
                    /* Apply pre-emphasis */
                    inp[i] = Inlines.SHL32(x, CeltConstants.SIG_SHIFT) - m;
                    m = Inlines.SHR32(Inlines.MULT16_16(coef0, x), 15 - CeltConstants.SIG_SHIFT);
                }
                mem.Val = m;
                return;
            }

            Nu = N / upsample;
            if (upsample != 1)
            {
                inp.MemSet(0, N);
            }
            for (i = 0; i < Nu; i++)
                inp[i * upsample] = pcmp[CC * i];


            for (i = 0; i < N; i++)
            {
                int x;
                x = (inp[i]);
                /* Apply pre-emphasis */
                inp[i] = Inlines.SHL32(x, CeltConstants.SIG_SHIFT) - m;
                m = Inlines.SHR32(Inlines.MULT16_16(coef0, x), 15 - CeltConstants.SIG_SHIFT);
            }

            mem.Val = m;
        }

        internal static void celt_preemphasis(Pointer<short> pcmp, Pointer<int> inp,
                                int N, int CC, int upsample, Pointer<int> coef, BoxedValue<int> mem, int clip)
        {
            int i;
            int coef0;
            int m;
            int Nu;

            coef0 = coef[0];
            m = mem.Val;

            /* Fast path for the normal 48kHz case and no clipping */
            if (coef[1] == 0 && upsample == 1 && clip == 0)
            {
                for (i = 0; i < N; i++)
                {
                    int x;
                    x = pcmp[CC * i];
                    /* Apply pre-emphasis */
                    inp[i] = Inlines.SHL32(x, CeltConstants.SIG_SHIFT) - m;
                    m = Inlines.SHR32(Inlines.MULT16_16(coef0, x), 15 - CeltConstants.SIG_SHIFT);
                }
                mem.Val = m;
                return;
            }

            Nu = N / upsample;
            if (upsample != 1)
            {
                inp.MemSet(0, N);
            }
            for (i = 0; i < Nu; i++)
                inp[i * upsample] = pcmp[CC * i];


            for (i = 0; i < N; i++)
            {
                int x;
                x = (inp[i]);
                /* Apply pre-emphasis */
                inp[i] = Inlines.SHL32(x, CeltConstants.SIG_SHIFT) - m;
                m = Inlines.SHR32(Inlines.MULT16_16(coef0, x), 15 - CeltConstants.SIG_SHIFT);
            }

            mem.Val = m;
        }

        internal static int l1_metric(Pointer<int> tmp, int N, int LM, int bias)
        {
            int i;
            int L1;
            L1 = 0;
            for (i = 0; i < N; i++)
            {
                L1 += Inlines.EXTEND32(Inlines.ABS32(tmp[i]));
            }

            /* When in doubt, prefer good freq resolution */
            L1 = Inlines.MAC16_32_Q15(L1, (LM * bias), (L1));
            return L1;

        }

        internal static int tf_analysis(CeltMode m, int len, int isTransient,
              Pointer<int> tf_res, int lambda, Pointer<int> X, int N0, int LM,
              BoxedValue<int> tf_sum, int tf_estimate, int tf_chan)
        {
            int i;
            int[] metric;
            int cost0;
            int cost1;
            int[] path0;
            int[] path1;
            int[] tmp;
            int[] tmp_1;
            int sel;
            int[] selcost = new int[2];
            int tf_select = 0;
            int bias;


            bias = Inlines.MULT16_16_Q14(Inlines.QCONST16(.04f, 15), Inlines.MAX16((short)(0 - Inlines.QCONST16(.25f, 14)), (Inlines.QCONST16(.5f, 14) - tf_estimate)));
            /*printf("%f ", bias);*/

            metric = new int[len];
            tmp = new int[(m.eBands[len] - m.eBands[len - 1]) << LM];
            tmp_1 = new int[(m.eBands[len] - m.eBands[len - 1]) << LM];
            path0 = new int[len];
            path1 = new int[len];

            tf_sum.Val = 0;
            for (i = 0; i < len; i++)
            {
                int k, N;
                int narrow;
                int L1, best_L1;
                int best_level = 0;
                N = (m.eBands[i + 1] - m.eBands[i]) << LM;
                /* band is too narrow to be split down to LM=-1 */
                narrow = ((m.eBands[i + 1] - m.eBands[i]) == 1) ? 1 : 0;
                X.Point(tf_chan * N0 + (m.eBands[i] << LM)).MemCopyTo(tmp, 0, N);
                /* Just add the right channel if we're in stereo */
                /*if (C==2)
                   for (j=0;j<N;j++)
                      tmp[j] = ADD16(SHR16(tmp[j], 1),SHR16(X[N0+j+(m.eBands[i]<<LM)], 1));*/
                L1 = l1_metric(tmp.GetPointer(), N, isTransient != 0 ? LM : 0, bias);
                best_L1 = L1;
                /* Check the -1 case for transients */
                if (isTransient != 0 && narrow == 0)
                {
                    Array.Copy(tmp, 0, tmp_1, 0, N);
                    Bands.haar1(tmp_1.GetPointer(), N >> LM, 1 << LM);
                    L1 = l1_metric(tmp_1.GetPointer(), N, LM + 1, bias);
                    if (L1 < best_L1)
                    {
                        best_L1 = L1;
                        best_level = -1;
                    }
                }
                /*printf ("%f ", L1);*/
                for (k = 0; k < LM + (!(isTransient != 0 || narrow != 0) ? 1 : 0); k++)
                {
                    int B;

                    if (isTransient != 0)
                        B = (LM - k - 1);
                    else
                        B = k + 1;

                    Bands.haar1(tmp.GetPointer(), N >> k, 1 << k);

                    L1 = l1_metric(tmp.GetPointer(), N, B, bias);

                    if (L1 < best_L1)
                    {
                        best_L1 = L1;
                        best_level = k + 1;
                    }
                }
                /*printf ("%d ", isTransient ? LM-best_level : best_level);*/
                /* metric is in Q1 to be able to select the mid-point (-0.5) for narrower bands */
                if (isTransient != 0)
                    metric[i] = 2 * best_level;
                else
                    metric[i] = -2 * best_level;
                tf_sum.Val += (isTransient != 0 ? LM : 0) - metric[i] / 2;
                /* For bands that can't be split to -1, set the metric to the half-way point to avoid
                   biasing the decision */
                if (narrow != 0 && (metric[i] == 0 || metric[i] == -2 * LM))
                    metric[i] -= 1;
                /*printf("%d ", metric[i]);*/
            }
            /*printf("\n");*/
            /* Search for the optimal tf resolution, including tf_select */
            tf_select = 0;
            for (sel = 0; sel < 2; sel++)
            {
                cost0 = 0;
                cost1 = isTransient != 0 ? 0 : lambda;
                for (i = 1; i < len; i++)
                {
                    int curr0, curr1;
                    curr0 = Inlines.IMIN(cost0, cost1 + lambda);
                    curr1 = Inlines.IMIN(cost0 + lambda, cost1);
                    cost0 = curr0 + Inlines.abs(metric[i] - 2 * Tables.tf_select_table[LM][4 * isTransient + 2 * sel + 0]);
                    cost1 = curr1 + Inlines.abs(metric[i] - 2 * Tables.tf_select_table[LM][4 * isTransient + 2 * sel + 1]);
                }
                cost0 = Inlines.IMIN(cost0, cost1);
                selcost[sel] = cost0;
            }
            /* For now, we're conservative and only allow tf_select=1 for transients.
             * If tests confirm it's useful for non-transients, we could allow it. */
            if (selcost[1] < selcost[0] && isTransient != 0)
                tf_select = 1;
            cost0 = 0;
            cost1 = isTransient != 0 ? 0 : lambda;
            /* Viterbi forward pass */
            for (i = 1; i < len; i++)
            {
                int curr0, curr1;
                int from0, from1;

                from0 = cost0;
                from1 = cost1 + lambda;
                if (from0 < from1)
                {
                    curr0 = from0;
                    path0[i] = 0;
                }
                else {
                    curr0 = from1;
                    path0[i] = 1;
                }

                from0 = cost0 + lambda;
                from1 = cost1;
                if (from0 < from1)
                {
                    curr1 = from0;
                    path1[i] = 0;
                }
                else {
                    curr1 = from1;
                    path1[i] = 1;
                }
                cost0 = curr0 + Inlines.abs(metric[i] - 2 * Tables.tf_select_table[LM][4 * isTransient + 2 * tf_select + 0]);
                cost1 = curr1 + Inlines.abs(metric[i] - 2 * Tables.tf_select_table[LM][4 * isTransient + 2 * tf_select + 1]);
            }
            tf_res[len - 1] = cost0 < cost1 ? 0 : 1;
            /* Viterbi backward pass to check the decisions */
            for (i = len - 2; i >= 0; i--)
            {
                if (tf_res[i + 1] == 1)
                    tf_res[i] = path1[i + 1];
                else
                    tf_res[i] = path0[i + 1];
            }
            /*printf("%d %f\n", *tf_sum, tf_estimate);*/

#if FUZZING
            Random rand = new Random();
            tf_select = rand.Next() & 0x1;
            tf_res[0] = rand.Next() & 0x1;
            for (i = 1; i < len; i++)
            {
                tf_res[i] = tf_res[i - 1] ^ ((rand.Next() & 0xF) == 0 ? 1 : 0);
            }
#endif
            return tf_select;
        }

        internal static void tf_encode(int start, int end, int isTransient, Pointer<int> tf_res, int LM, int tf_select, EntropyCoder enc)
        {
            int curr, i;
            int tf_select_rsv;
            int tf_changed;
            int logp;
            uint budget;
            uint tell;
            budget = enc.storage * 8;
            tell = (uint)enc.tell();
            logp = isTransient != 0 ? 2 : 4;
            /* Reserve space to code the tf_select decision. */
            tf_select_rsv = (LM > 0 && tell + logp + 1 <= budget) ? 1 : 0;
            budget -= (uint)tf_select_rsv;
            curr = tf_changed = 0;
            for (i = start; i < end; i++)
            {
                if (tell + logp <= budget)
                {
                    enc.enc_bit_logp(tf_res[i] ^ curr, (uint)logp);
                    tell = (uint)enc.tell();
                    curr = tf_res[i];
                    tf_changed |= curr;
                }
                else
                    tf_res[i] = curr;
                logp = isTransient != 0 ? 4 : 5;
            }
            /* Only code tf_select if it would actually make a difference. */
            if (tf_select_rsv != 0 &&
                  Tables.tf_select_table[LM][4 * isTransient + 0 + tf_changed] !=
                  Tables.tf_select_table[LM][4 * isTransient + 2 + tf_changed])
                enc.enc_bit_logp(tf_select, 1);
            else
                tf_select = 0;
            for (i = start; i < end; i++)
                tf_res[i] = Tables.tf_select_table[LM][4 * isTransient + 2 * tf_select + tf_res[i]];
            /*for(i=0;i<end;i++)printf("%d ", isTransient ? tf_res[i] : LM+tf_res[i]);printf("\n");*/
        }


        internal static int alloc_trim_analysis(CeltMode m, Pointer<int> X,
              Pointer<int> bandLogE, int end, int LM, int C, int N0,
              AnalysisInfo analysis, BoxedValue<int> stereo_saving, int tf_estimate,
              int intensity, int surround_trim)
        {
            int i;
            int diff = 0;
            int c;
            int trim_index;
            int trim = Inlines.QCONST16(5.0f, 8);
            int logXC, logXC2;
            if (C == 2)
            {
                int sum = 0; /* Q10 */
                int minXC; /* Q10 */
                           /* Compute inter-channel correlation for low frequencies */
                for (i = 0; i < 8; i++)
                {
                    int partial;
                    partial = Kernels.celt_inner_prod(X.Data, X.Offset + (m.eBands[i] << LM), X.Data, X.Offset + (N0 + (m.eBands[i] << LM)),
                          (m.eBands[i + 1] - m.eBands[i]) << LM);
                    sum = Inlines.ADD16(sum, Inlines.EXTRACT16(Inlines.SHR32(partial, 18)));
                }
                sum = Inlines.MULT16_16_Q15(Inlines.QCONST16(1.0f / 8, 15), sum);
                sum = Inlines.MIN16(Inlines.QCONST16(1.0f, 10), Inlines.ABS32(sum));
                minXC = sum;
                for (i = 8; i < intensity; i++)
                {
                    int partial;
                    partial = Kernels.celt_inner_prod(X.Data, X.Offset + (m.eBands[i] << LM), X.Data, X.Offset + (N0 + (m.eBands[i] << LM)),
                          (m.eBands[i + 1] - m.eBands[i]) << LM);
                    minXC = Inlines.MIN16(minXC, Inlines.ABS16(Inlines.EXTRACT16(Inlines.SHR32(partial, 18))));
                }
                minXC = Inlines.MIN16(Inlines.QCONST16(1.0f, 10), Inlines.ABS32(minXC));
                /*printf ("%f\n", sum);*/
                /* mid-side savings estimations based on the LF average*/
                logXC = Inlines.celt_log2(Inlines.QCONST32(1.001f, 20) - Inlines.MULT16_16(sum, sum));
                /* mid-side savings estimations based on min correlation */
                logXC2 = Inlines.MAX16(Inlines.HALF16(logXC), Inlines.celt_log2(Inlines.QCONST32(1.001f, 20) - Inlines.MULT16_16(minXC, minXC)));
                /* Compensate for Q20 vs Q14 input and convert output to Q8 */
                logXC = (Inlines.PSHR32(logXC - Inlines.QCONST16(6.0f, CeltConstants.DB_SHIFT), CeltConstants.DB_SHIFT - 8));
                logXC2 = (Inlines.PSHR32(logXC2 - Inlines.QCONST16(6.0f, CeltConstants.DB_SHIFT), CeltConstants.DB_SHIFT - 8));

                trim += Inlines.MAX16((0 - Inlines.QCONST16(4.0f, 8)), Inlines.MULT16_16_Q15(Inlines.QCONST16(.75f, 15), logXC));
                stereo_saving.Val = Inlines.MIN16((stereo_saving.Val + Inlines.QCONST16(0.25f, 8)), (0 - Inlines.HALF16(logXC2)));
            }

            /* Estimate spectral tilt */
            c = 0; do
            {
                for (i = 0; i < end - 1; i++)
                {
                    diff += bandLogE[i + c * m.nbEBands] * (int)(2 + 2 * i - end);
                }
            } while (++c < C);
            diff /= C * (end - 1);
            /*printf("%f\n", diff);*/
            trim -= Inlines.MAX16(Inlines.NEG16(Inlines.QCONST16(2.0f, 8)), Inlines.MIN16(Inlines.QCONST16(2.0f, 8), (Inlines.SHR16((diff + Inlines.QCONST16(1.0f, CeltConstants.DB_SHIFT)), CeltConstants.DB_SHIFT - 8) / 6)));
            trim -= Inlines.SHR16(surround_trim, CeltConstants.DB_SHIFT - 8);
            trim = (trim - 2 * Inlines.SHR16(tf_estimate, 14 - 8));
#if ENABLE_ANALYSIS
            if (analysis.valid != 0)
            {
                trim -= Inlines.MAX16(-Inlines.QCONST16(2.0f, 8), Inlines.MIN16(Inlines.QCONST16(2.0f, 8),
                      (int)(Inlines.QCONST16(2.0f, 8) * (analysis.tonality_slope + .05f))));
            }
#endif
            trim_index = Inlines.PSHR32(trim, 8);
            trim_index = Inlines.IMAX(0, Inlines.IMIN(10, trim_index));
            /*printf("%d\n", trim_index);*/
#if FUZZING
            trim_index = new Random().Next() % 11;
#endif
            return trim_index;
        }

        internal static int stereo_analysis(CeltMode m, Pointer<int> X,
              int LM, int N0)
        {
            int i;
            int thetas;
            int sumLR = CeltConstants.EPSILON, sumMS = CeltConstants.EPSILON;

            /* Use the L1 norm to model the entropy of the L/R signal vs the M/S signal */
            for (i = 0; i < 13; i++)
            {
                int j;
                for (j = m.eBands[i] << LM; j < m.eBands[i + 1] << LM; j++)
                {
                    int L, R, M, S;
                    /* We cast to 32-bit first because of the -32768 case */
                    L = Inlines.EXTEND32(X[j]);
                    R = Inlines.EXTEND32(X[N0 + j]);
                    M = Inlines.ADD32(L, R);
                    S = Inlines.SUB32(L, R);
                    sumLR = Inlines.ADD32(sumLR, Inlines.ADD32(Inlines.ABS32(L), Inlines.ABS32(R)));
                    sumMS = Inlines.ADD32(sumMS, Inlines.ADD32(Inlines.ABS32(M), Inlines.ABS32(S)));
                }
            }
            sumMS = Inlines.MULT16_32_Q15(Inlines.QCONST16(0.707107f, 15), sumMS);
            thetas = 13;
            /* We don't need thetas for lower bands with LM<=1 */
            if (LM <= 1)
                thetas -= 8;
            return (Inlines.MULT16_32_Q15(((m.eBands[13] << (LM + 1)) + thetas), sumMS)
                  > Inlines.MULT16_32_Q15((m.eBands[13] << (LM + 1)), sumLR)) ? 1 : 0;
        }

        internal static int median_of_5(Pointer<int> x)
        {
            int t0, t1, t2, t3, t4;
            t2 = x[2];
            if (x[0] > x[1])
            {
                t0 = x[1];
                t1 = x[0];
            }
            else {
                t0 = x[0];
                t1 = x[1];
            }
            if (x[3] > x[4])
            {
                t3 = x[4];
                t4 = x[3];
            }
            else {
                t3 = x[3];
                t4 = x[4];
            }
            if (t0 > t3)
            {
                // swap the pairs
                int tmp = t3;
                t3 = t0;
                t0 = tmp;
                tmp = t4;
                t4 = t1;
                t1 = tmp;
            }
            if (t2 > t1)
            {
                if (t1 < t3)
                    return Inlines.MIN16(t2, t3);
                else
                    return Inlines.MIN16(t4, t1);
            }
            else {
                if (t2 < t3)
                    return Inlines.MIN16(t1, t3);
                else
                    return Inlines.MIN16(t2, t4);
            }
        }

        internal static int median_of_3(Pointer<int> x)
        {
            int t0, t1, t2;
            if (x[0] > x[1])
            {
                t0 = x[1];
                t1 = x[0];
            }
            else {
                t0 = x[0];
                t1 = x[1];
            }
            t2 = x[2];
            if (t1 < t2)
                return t1;
            else if (t0 < t2)
                return t2;
            else
                return t0;
        }

        internal static int dynalloc_analysis(Pointer<int> bandLogE, Pointer<int> bandLogE2,
              int nbEBands, int start, int end, int C, Pointer<int> offsets, int lsb_depth, Pointer<short> logN,
              int isTransient, int vbr, int constrained_vbr, Pointer<short> eBands, int LM,
              int effectiveBytes, BoxedValue<int> tot_boost_, int lfe, Pointer<int> surround_dynalloc)
        {
            int i, c;
            int tot_boost = 0;
            int maxDepth;
            int[] follower = new int[C * nbEBands];
            int[] noise_floor = new int[C * nbEBands];

            offsets.MemSet(0, nbEBands);
            /* Dynamic allocation code */
            maxDepth = (0 - Inlines.QCONST16(31.9f, CeltConstants.DB_SHIFT));
            for (i = 0; i < end; i++)
            {
                /* Noise floor must take into account eMeans, the depth, the width of the bands
                   and the preemphasis filter (approx. square of bark band ID) */
                noise_floor[i] = (Inlines.MULT16_16(Inlines.QCONST16(0.0625f, CeltConstants.DB_SHIFT), logN[i])
                      + Inlines.QCONST16(.5f, CeltConstants.DB_SHIFT) + Inlines.SHL16((9 - lsb_depth), CeltConstants.DB_SHIFT) - Inlines.SHL16(Tables.eMeans[i], 6)
                      + Inlines.MULT16_16(Inlines.QCONST16(0.0062f, CeltConstants.DB_SHIFT), (i + 5) * (i + 5)));
            }
            c = 0; do
            {
                for (i = 0; i < end; i++)
                    maxDepth = Inlines.MAX16(maxDepth, (bandLogE[c * nbEBands + i] - noise_floor[i]));
            } while (++c < C);
            /* Make sure that dynamic allocation can't make us bust the budget */
            if (effectiveBytes > 50 && LM >= 1 && lfe == 0)
            {
                int last = 0;
                c = 0; do
                {
                    int offset;
                    int tmp;
                    Pointer<int> f = follower.GetPointer(c * nbEBands);
                    f[0] = bandLogE2[c * nbEBands];
                    for (i = 1; i < end; i++)
                    {
                        /* The last band to be at least 3 dB higher than the previous one
                           is the last we'll consider. Otherwise, we run into problems on
                           bandlimited signals. */
                        if (bandLogE2[c * nbEBands + i] > bandLogE2[c * nbEBands + i - 1] + Inlines.QCONST16(0.5f, CeltConstants.DB_SHIFT))
                            last = i;
                        f[i] = Inlines.MIN16((f[i - 1] + Inlines.QCONST16(1.5f, CeltConstants.DB_SHIFT)), bandLogE2[c * nbEBands + i]);
                    }
                    for (i = last - 1; i >= 0; i--)
                        f[i] = Inlines.MIN16(f[i], Inlines.MIN16((f[i + 1] + Inlines.QCONST16(2.0f, CeltConstants.DB_SHIFT)), bandLogE2[c * nbEBands + i]));

                    /* Combine with a median filter to avoid dynalloc triggering unnecessarily.
                       The "offset" value controls how conservative we are -- a higher offset
                       reduces the impact of the median filter and makes dynalloc use more bits. */
                    offset = Inlines.QCONST16(1.0f, CeltConstants.DB_SHIFT);
                    for (i = 2; i < end - 2; i++)
                        f[i] = Inlines.MAX16(f[i], median_of_5(bandLogE2.Point(c * nbEBands + i - 2)) - offset);
                    tmp = median_of_3(bandLogE2.Point(c * nbEBands)) - offset;
                    f[0] = Inlines.MAX16(f[0], tmp);
                    f[1] = Inlines.MAX16(f[1], tmp);
                    tmp = median_of_3(bandLogE2.Point(c * nbEBands + end - 3)) - offset;
                    f[end - 2] = Inlines.MAX16(f[end - 2], tmp);
                    f[end - 1] = Inlines.MAX16(f[end - 1], tmp);

                    for (i = 0; i < end; i++)
                        f[i] = Inlines.MAX16(f[i], noise_floor[i]);
                } while (++c < C);
                if (C == 2)
                {
                    for (i = start; i < end; i++)
                    {
                        /* Consider 24 dB "cross-talk" */
                        follower[nbEBands + i] = Inlines.MAX16(follower[nbEBands + i], follower[i] - Inlines.QCONST16(4.0f, CeltConstants.DB_SHIFT));
                        follower[i] = Inlines.MAX16(follower[i], follower[nbEBands + i] - Inlines.QCONST16(4.0f, CeltConstants.DB_SHIFT));
                        follower[i] = Inlines.HALF16(Inlines.MAX16(0, bandLogE[i] - follower[i]) + Inlines.MAX16(0, bandLogE[nbEBands + i] - follower[nbEBands + i]));
                    }
                }
                else {
                    for (i = start; i < end; i++)
                    {
                        follower[i] = Inlines.MAX16(0, bandLogE[i] - follower[i]);
                    }
                }
                for (i = start; i < end; i++)
                    follower[i] = Inlines.MAX16(follower[i], surround_dynalloc[i]);
                /* For non-transient CBR/CVBR frames, halve the dynalloc contribution */
                if ((vbr == 0 || constrained_vbr != 0) && isTransient == 0)
                {
                    for (i = start; i < end; i++)
                        follower[i] = Inlines.HALF16(follower[i]);
                }
                for (i = start; i < end; i++)
                {
                    int width;
                    int boost;
                    int boost_bits;

                    if (i < 8)
                        follower[i] *= 2;
                    if (i >= 12)
                        follower[i] = Inlines.HALF16(follower[i]);
                    follower[i] = Inlines.MIN16(follower[i], Inlines.QCONST16(4, CeltConstants.DB_SHIFT));

                    width = C * (eBands[i + 1] - eBands[i]) << LM;
                    if (width < 6)
                    {
                        boost = (int)Inlines.SHR32((follower[i]), CeltConstants.DB_SHIFT);
                        boost_bits = boost * width << EntropyCoder.BITRES;
                    }
                    else if (width > 48)
                    {
                        boost = (int)Inlines.SHR32((follower[i]) * 8, CeltConstants.DB_SHIFT);
                        boost_bits = (boost * width << EntropyCoder.BITRES) / 8;
                    }
                    else {
                        boost = (int)Inlines.SHR32((follower[i]) * width / 6, CeltConstants.DB_SHIFT);
                        boost_bits = boost * 6 << EntropyCoder.BITRES;
                    }
                    /* For CBR and non-transient CVBR frames, limit dynalloc to 1/4 of the bits */
                    if ((vbr == 0 || (constrained_vbr != 0 && isTransient == 0))
                          && (tot_boost + boost_bits) >> EntropyCoder.BITRES >> 3 > effectiveBytes / 4)
                    {
                        int cap = ((effectiveBytes / 4) << EntropyCoder.BITRES << 3);
                        offsets[i] = cap - tot_boost;
                        tot_boost = cap;
                        break;
                    }
                    else {
                        offsets[i] = boost;
                        tot_boost += boost_bits;
                    }
                }
            }

            tot_boost_.Val = tot_boost;

            return maxDepth;
        }

        internal static void deemphasis(Pointer<Pointer<int>> input, Pointer<short> pcm, int N, int C, int downsample, Pointer<int> coef,
              Pointer<int> mem, int accum)
        {
            int c;
            int Nd;
            int apply_downsampling = 0;
            int coef0;
            int[] scratch = new int[N];
            coef0 = coef[0];
            Nd = N / downsample;
            c = 0; do
            {
                int j;
                Pointer<int> x;
                Pointer<short> y;
                int m = mem[c];
                x = input[c];
                y = pcm.Point(c);
                if (downsample > 1)
                {
                    /* Shortcut for the standard (non-custom modes) case */
                    for (j = 0; j < N; j++)
                    {
                        int tmp = x[j] + m + CeltConstants.VERY_SMALL;
                        m = Inlines.MULT16_32_Q15(coef0, tmp);
                        scratch[j] = tmp;
                    }
                    apply_downsampling = 1;
                }
                else {
                    /* Shortcut for the standard (non-custom modes) case */
                    if (accum != 0) // should never hit this branch?
                    {
                        for (j = 0; j < N; j++)
                        {
                            int tmp = x[j] + m + CeltConstants.VERY_SMALL;
                            m = Inlines.MULT16_32_Q15(coef0, tmp);
                            y[j * C] = Inlines.SAT16(Inlines.ADD32(y[j * C], Inlines.SIG2WORD16(tmp)));
                        }
                    }
                    else
                    {
                        for (j = 0; j < N; j++)
                        {
                            int tmp = unchecked(x[j] + m + CeltConstants.VERY_SMALL); // Opus bug: This can overflow.
                            if (x[j] > 0 && m > 0 && tmp < 0) // This is a hack to saturate to INT_MAXVALUE
                            {
                                tmp = int.MaxValue;
                                m = int.MaxValue;
                            }
                            else
                            {
                                m = Inlines.MULT16_32_Q15(coef0, tmp);
                            }
                            y[j * C] = Inlines.SIG2WORD16(tmp);
                        }
                    }
                }
                mem[c] = m;

                if (apply_downsampling != 0)
                {
                    /* Perform down-sampling */
                    {
                        for (j = 0; j < Nd; j++)
                            y[j * C] = Inlines.SIG2WORD16(scratch[j * downsample]);
                    }
                }
            } while (++c < C);

        }

        internal static void celt_synthesis(CeltMode mode, Pointer<int> X, Pointer<Pointer<int>> out_syn,
                            Pointer<int> oldBandE, int start, int effEnd, int C, int CC,
                            int isTransient, int LM, int downsample,
                            int silence)
        {
            int c, i;
            int M;
            int b;
            int B;
            int N, NB;
            int shift;
            int nbEBands;
            int overlap;
            int[] freq;

            overlap = mode.overlap;
            nbEBands = mode.nbEBands;
            N = mode.shortMdctSize << LM;
            freq = new int[N]; /**< Interleaved signal MDCTs */
            M = 1 << LM;

            if (isTransient != 0)
            {
                B = M;
                NB = mode.shortMdctSize;
                shift = mode.maxLM;
            }
            else {
                B = 1;
                NB = mode.shortMdctSize << LM;
                shift = mode.maxLM - LM;
            }

            if (CC == 2 && C == 1)
            {
                /* Copying a mono streams to two channels */
                Pointer<int> freq2;
                Bands.denormalise_bands(mode, X, freq.GetPointer(), oldBandE, start, effEnd, M,
                      downsample, silence);
                /* Store a temporary copy in the output buffer because the IMDCT destroys its input. */
                freq2 = out_syn[1].Point(overlap / 2);
                freq.GetPointer().MemCopyTo(freq2, N);
                for (b = 0; b < B; b++)
                    MDCT.clt_mdct_backward(mode.mdct, freq2.Point(b), out_syn[0].Point(NB * b), mode.window.GetPointer(), overlap, shift, B);
                for (b = 0; b < B; b++)
                    MDCT.clt_mdct_backward(mode.mdct, freq.GetPointer(b), out_syn[1].Point(NB * b), mode.window.GetPointer(), overlap, shift, B);
            }
            else if (CC == 1 && C == 2)
            {
                /* Downmixing a stereo stream to mono */
                Pointer<int> freq2;
                freq2 = out_syn[0].Point(overlap / 2);
                Bands.denormalise_bands(mode, X, freq.GetPointer(), oldBandE, start, effEnd, M,
                      downsample, silence);
                /* Use the output buffer as temp array before downmixing. */
                Bands.denormalise_bands(mode, X.Point(N), freq2, oldBandE.Point(nbEBands), start, effEnd, M,
                      downsample, silence);
                for (i = 0; i < N; i++)
                    freq[i] = Inlines.HALF32(Inlines.ADD32(freq[i], freq2[i]));
                for (b = 0; b < B; b++)
                    MDCT.clt_mdct_backward(mode.mdct, freq.GetPointer(b), out_syn[0].Point(NB * b), mode.window.GetPointer(), overlap, shift, B);
            }
            else {
                /* Normal case (mono or stereo) */
                c = 0; do
                {
                    Bands.denormalise_bands(mode, X.Point(c * N), freq.GetPointer(), oldBandE.Point(c * nbEBands), start, effEnd, M,
                          downsample, silence);
                    for (b = 0; b < B; b++)
                        MDCT.clt_mdct_backward(mode.mdct, freq.GetPointer(b), out_syn[c].Point(NB * b), mode.window.GetPointer(), overlap, shift, B);
                } while (++c < CC);
            }

        }

        internal static void tf_decode(int start, int end, int isTransient, Pointer<int> tf_res, int LM, EntropyCoder dec)
        {
            int i, curr, tf_select;
            int tf_select_rsv;
            int tf_changed;
            int logp;
            uint budget;
            uint tell;

            budget = dec.storage * 8;
            tell = (uint)dec.tell();
            logp = isTransient != 0 ? 2 : 4;
            tf_select_rsv = (LM > 0 && tell + logp + 1 <= budget) ? 1 : 0;
            budget -= (uint)tf_select_rsv;
            tf_changed = curr = 0;
            for (i = start; i < end; i++)
            {
                if (tell + logp <= budget)
                {
                    curr ^= dec.dec_bit_logp((uint)logp);
                    tell = (uint)dec.tell();
                    tf_changed |= curr;
                }
                tf_res[i] = curr;
                logp = isTransient != 0 ? 4 : 5;
            }
            tf_select = 0;
            if (tf_select_rsv != 0 &&
              Tables.tf_select_table[LM][4 * isTransient + 0 + tf_changed] !=
              Tables.tf_select_table[LM][4 * isTransient + 2 + tf_changed])
            {
                tf_select = dec.dec_bit_logp(1);
            }
            for (i = start; i < end; i++)
            {
                tf_res[i] = Tables.tf_select_table[LM][4 * isTransient + 2 * tf_select + tf_res[i]];
            }
        }

        internal static int celt_plc_pitch_search(Pointer<Pointer<int>> decode_mem, int C)
        {
            BoxedValue<int> pitch_index = new BoxedValue<int>();
            int[] lp_pitch_buf = new int[CeltConstants.DECODE_BUFFER_SIZE >> 1];
            Pitch.pitch_downsample(decode_mem, lp_pitch_buf,
                  CeltConstants.DECODE_BUFFER_SIZE, C);
            Pitch.pitch_search(lp_pitch_buf.GetPointer(CeltConstants.PLC_PITCH_LAG_MAX >> 1), lp_pitch_buf.GetPointer(),
                  CeltConstants.DECODE_BUFFER_SIZE - CeltConstants.PLC_PITCH_LAG_MAX,
                  CeltConstants.PLC_PITCH_LAG_MAX - CeltConstants.PLC_PITCH_LAG_MIN, pitch_index);
            pitch_index.Val = CeltConstants.PLC_PITCH_LAG_MAX - pitch_index.Val;

            return pitch_index.Val;
        }

        internal static int resampling_factor(int rate)
        {
            int ret;
            switch (rate)
            {
                case 48000:
                    ret = 1;
                    break;
                case 24000:
                    ret = 2;
                    break;
                case 16000:
                    ret = 3;
                    break;
                case 12000:
                    ret = 4;
                    break;
                case 8000:
                    ret = 6;
                    break;
                default:
                    //Inlines.OpusAssert(false);
                    ret = 0;
                    break;
            }
            return ret;
        }

        internal static void comb_filter_const(Pointer<int> y, Pointer<int> x, int T, int N,
              int g10, int g11, int g12)
        {
            int x0, x1, x2, x3, x4;
            int i;
            x4 = x[-T - 2];
            x3 = x[-T - 1];
            x2 = x[-T];
            x1 = x[-T + 1];
            for (i = 0; i < N; i++)
            {
                x0 = x[i - T + 2];
                y[i] = x[i]
                        + Inlines.MULT16_32_Q15(g10, x2)
                        + Inlines.MULT16_32_Q15(g11, Inlines.ADD32(x1, x3))
                        + Inlines.MULT16_32_Q15(g12, Inlines.ADD32(x0, x4));
                x4 = x3;
                x3 = x2;
                x2 = x1;
                x1 = x0;
            }

        }

        internal static void comb_filter(Pointer<int> y, Pointer<int> x, int T0, int T1, int N,
              int g0, int g1, int tapset0, int tapset1,
            Pointer<int> window, int overlap)
        {
            int i;
            /* printf ("%d %d %f %f\n", T0, T1, g0, g1); */
            int g00, g01, g02, g10, g11, g12;
            int x0, x1, x2, x3, x4;
            short[][] gains = {
                new short[]{ Inlines.QCONST16(0.3066406250f, 15), Inlines.QCONST16(0.2170410156f, 15), Inlines.QCONST16(0.1296386719f, 15)},
                new short[]{ Inlines.QCONST16(0.4638671875f, 15), Inlines.QCONST16(0.2680664062f, 15), Inlines.QCONST16(0.0f, 15)},
                new short[]{ Inlines.QCONST16(0.7998046875f, 15), Inlines.QCONST16(0.1000976562f, 15), Inlines.QCONST16(0.0f, 15)}
            };

            if (g0 == 0 && g1 == 0)
            {
                /* OPT: Happens to work without the OPUS_MOVE(), but only because the current encoder already copies x to y */
                if (x != y)
                {
                    x.MemMoveTo(y, N);
                }

                return;
            }
            g00 = Inlines.MULT16_16_P15(g0, gains[tapset0][0]);
            g01 = Inlines.MULT16_16_P15(g0, gains[tapset0][1]);
            g02 = Inlines.MULT16_16_P15(g0, gains[tapset0][2]);
            g10 = Inlines.MULT16_16_P15(g1, gains[tapset1][0]);
            g11 = Inlines.MULT16_16_P15(g1, gains[tapset1][1]);
            g12 = Inlines.MULT16_16_P15(g1, gains[tapset1][2]);
            x1 = x[-T1 + 1];
            x2 = x[-T1];
            x3 = x[-T1 - 1];
            x4 = x[-T1 - 2];
            /* If the filter didn't change, we don't need the overlap */
            if (g0 == g1 && T0 == T1 && tapset0 == tapset1)
                overlap = 0;
            for (i = 0; i < overlap; i++)
            {
                int f;
                x0 = x[i - T1 + 2];
                f = Inlines.MULT16_16_Q15(window[i], window[i]);
                y[i] = x[i]
                    + Inlines.MULT16_32_Q15(Inlines.MULT16_16_Q15(Inlines.CHOP16(CeltConstants.Q15ONE - f), g00), x[i - T0])
                    + Inlines.MULT16_32_Q15(Inlines.MULT16_16_Q15(Inlines.CHOP16(CeltConstants.Q15ONE - f), g01), Inlines.ADD32(x[i - T0 + 1], x[i - T0 - 1]))
                    + Inlines.MULT16_32_Q15(Inlines.MULT16_16_Q15(Inlines.CHOP16(CeltConstants.Q15ONE - f), g02), Inlines.ADD32(x[i - T0 + 2], x[i - T0 - 2]))
                    + Inlines.MULT16_32_Q15(Inlines.MULT16_16_Q15(f, g10), x2)
                    + Inlines.MULT16_32_Q15(Inlines.MULT16_16_Q15(f, g11), Inlines.ADD32(x1, x3))
                    + Inlines.MULT16_32_Q15(Inlines.MULT16_16_Q15(f, g12), Inlines.ADD32(x0, x4));
                x4 = x3;
                x3 = x2;
                x2 = x1;
                x1 = x0;

            }
            if (g1 == 0)
            {
                /* OPT: Happens to work without the OPUS_MOVE(), but only because the current encoder already copies x to y */
                if (x != y)
                {
                    //OPUS_MOVE(y + overlap, x + overlap, N - overlap);
                    x.Point(overlap).MemMoveTo(y.Point(overlap), N - overlap);
                }
                return;
            }

            /* Compute the part with the constant filter. */
            comb_filter_const(y.Point(i), x.Point(i), T1, N - i, g10, g11, g12);
        }

        private static readonly sbyte[][] tf_select_table = {
              new sbyte[]{0, -1, 0, -1,    0,-1, 0,-1},
              new sbyte[]{0, -1, 0, -2,    1, 0, 1,-1},
              new sbyte[]{0, -2, 0, -3,    2, 0, 1,-1},
              new sbyte[]{0, -2, 0, -3,    3, 0, 1,-1},
        };
        
        internal static void init_caps(CeltMode m, Pointer<int> cap, int LM, int C)
        {
            int i;
            for (i = 0; i < m.nbEBands; i++)
            {
                int N;
                N = (m.eBands[i + 1] - m.eBands[i]) << LM;
                cap[i] = (m.cache.caps[m.nbEBands * (2 * LM + C - 1) + i] + 64) * C * N >> 2;
            }
        }
    }
}
