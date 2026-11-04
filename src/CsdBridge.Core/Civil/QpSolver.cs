using System;
using System.Collections.Generic;

namespace CsdBridge.Core.Civil
{
    /// <summary>
    /// A small dense quadratic-program solver:
    ///   minimise ½xᵀPx + qᵀx  subject to  l ≤ Ax ≤ u
    /// using the ADMM splitting from OSQP (Stellato et al. 2020), with row scaling and
    /// adaptive step size. Equalities are rows with l = u. Problems here are tiny
    /// (a few dozen IP levels), so dense Cholesky factorisation is fine.
    /// </summary>
    internal static class QpSolver
    {
        internal sealed class Result
        {
            public double[] X { get; set; } = Array.Empty<double>();

            /// <summary>Largest constraint violation of X (in the rows' original units).</summary>
            public double MaxViolation { get; set; }

            public bool Converged { get; set; }

            public int Iterations { get; set; }
        }

        public static Result Solve(double[,] p, double[] q, IReadOnlyList<double[]> a, double[] lower, double[] upper, double[]? x0 = null, int maxIterations = 40000)
        {
            int n = q.Length;
            int m = a.Count;

            // Scale each row to unit length so rows with tiny coefficients (1/spacing) and
            // rows with large ones converge at the same rate.
            var rows = new double[m][];
            var l = new double[m];
            var u = new double[m];
            for (int i = 0; i < m; i++)
            {
                double norm = 0;
                for (int j = 0; j < n; j++)
                {
                    norm += a[i][j] * a[i][j];
                }

                norm = Math.Sqrt(norm);
                if (norm < 1e-14)
                {
                    norm = 1;
                }

                rows[i] = new double[n];
                for (int j = 0; j < n; j++)
                {
                    rows[i][j] = a[i][j] / norm;
                }

                l[i] = double.IsNegativeInfinity(lower[i]) ? double.NegativeInfinity : lower[i] / norm;
                u[i] = double.IsPositiveInfinity(upper[i]) ? double.PositiveInfinity : upper[i] / norm;
            }

            const double sigma = 1e-6;
            const double alpha = 1.6;
            const double epsAbs = 1e-9;
            const double epsRel = 1e-8;
            double rho = 0.1;

            var x = new double[n];
            if (x0 != null)
            {
                Array.Copy(x0, x, n);
            }

            var z = new double[m];
            var y = new double[m];
            var ax = new double[m];
            MultiplyRows(rows, x, ax);
            for (int i = 0; i < m; i++)
            {
                z[i] = Clip(ax[i], l[i], u[i]);
            }

            double[] rhoVec = RhoVector(l, u, rho);
            double[,] chol = Factor(p, rows, rhoVec, sigma);

            var rhs = new double[n];
            var xt = new double[n];
            var zt = new double[m];
            var px = new double[n];
            var aty = new double[n];
            bool converged = false;
            int iter;
            for (iter = 1; iter <= maxIterations; iter++)
            {
                for (int j = 0; j < n; j++)
                {
                    rhs[j] = sigma * x[j] - q[j];
                }

                for (int i = 0; i < m; i++)
                {
                    double w = rhoVec[i] * z[i] - y[i];
                    double[] r = rows[i];
                    for (int j = 0; j < n; j++)
                    {
                        rhs[j] += r[j] * w;
                    }
                }

                CholeskySolve(chol, rhs, xt);
                MultiplyRows(rows, xt, zt);

                for (int j = 0; j < n; j++)
                {
                    x[j] = alpha * xt[j] + (1 - alpha) * x[j];
                }

                for (int i = 0; i < m; i++)
                {
                    double relaxed = alpha * zt[i] + (1 - alpha) * z[i];
                    double zNew = Clip(relaxed + y[i] / rhoVec[i], l[i], u[i]);
                    y[i] += rhoVec[i] * (relaxed - zNew);
                    z[i] = zNew;
                }

                if (iter % 25 != 0)
                {
                    continue;
                }

                // Residuals.
                MultiplyRows(rows, x, ax);
                Multiply(p, x, px);
                Array.Clear(aty, 0, n);
                for (int i = 0; i < m; i++)
                {
                    for (int j = 0; j < n; j++)
                    {
                        aty[j] += rows[i][j] * y[i];
                    }
                }

                double rPrim = 0, scalePrim = 0;
                for (int i = 0; i < m; i++)
                {
                    rPrim = Math.Max(rPrim, Math.Abs(ax[i] - z[i]));
                    scalePrim = Math.Max(scalePrim, Math.Max(Math.Abs(ax[i]), Math.Abs(z[i])));
                }

                double rDual = 0, scaleDual = 0;
                for (int j = 0; j < n; j++)
                {
                    rDual = Math.Max(rDual, Math.Abs(px[j] + q[j] + aty[j]));
                    scaleDual = Math.Max(scaleDual, Math.Max(Math.Abs(px[j]), Math.Max(Math.Abs(aty[j]), Math.Abs(q[j]))));
                }

                if (rPrim <= epsAbs + epsRel * scalePrim && rDual <= epsAbs + epsRel * scaleDual)
                {
                    converged = true;
                    break;
                }

                // Rebalance the step size when the residuals drift apart.
                if (iter % 100 == 0)
                {
                    double ratio = Math.Sqrt((rPrim / Math.Max(scalePrim, 1e-12)) / Math.Max(rDual / Math.Max(scaleDual, 1e-12), 1e-30));
                    double newRho = Math.Min(Math.Max(rho * ratio, 1e-6), 1e6);
                    if (newRho > rho * 5 || newRho < rho / 5)
                    {
                        // y is the unscaled dual, so it stays valid when rho changes.
                        rho = newRho;
                        rhoVec = RhoVector(l, u, rho);
                        chol = Factor(p, rows, rhoVec, sigma);
                    }
                }
            }

            // Violation measured on the caller's rows.
            double violation = 0;
            for (int i = 0; i < m; i++)
            {
                double v = 0;
                for (int j = 0; j < n; j++)
                {
                    v += a[i][j] * x[j];
                }

                if (v < lower[i])
                {
                    violation = Math.Max(violation, lower[i] - v);
                }
                else if (v > upper[i])
                {
                    violation = Math.Max(violation, v - upper[i]);
                }
            }

            return new Result { X = x, MaxViolation = violation, Converged = converged, Iterations = Math.Min(iter, maxIterations) };
        }

        /// <summary>Solve Kx = b for symmetric positive definite K (dense Cholesky). Returns null if K is not positive definite.</summary>
        public static double[]? SolveSpd(double[,] k, double[] b)
        {
            int n = b.Length;
            double[,]? chol = TryCholesky(k);
            if (chol == null)
            {
                return null;
            }

            var x = new double[n];
            CholeskySolve(chol, b, x);
            return x;
        }

        private static double[] RhoVector(double[] l, double[] u, double rho)
        {
            var v = new double[l.Length];
            for (int i = 0; i < l.Length; i++)
            {
                if (double.IsNegativeInfinity(l[i]) && double.IsPositiveInfinity(u[i]))
                {
                    v[i] = 1e-6;
                }
                else if (Math.Abs(u[i] - l[i]) < 1e-12)
                {
                    v[i] = 1e3 * rho; // equality
                }
                else
                {
                    v[i] = rho;
                }
            }

            return v;
        }

        private static double[,] Factor(double[,] p, double[][] rows, double[] rhoVec, double sigma)
        {
            int n = p.GetLength(0);
            var k = new double[n, n];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    k[i, j] = p[i, j];
                }

                k[i, i] += sigma;
            }

            for (int r = 0; r < rows.Length; r++)
            {
                double[] row = rows[r];
                for (int i = 0; i < n; i++)
                {
                    if (row[i] == 0)
                    {
                        continue;
                    }

                    for (int j = 0; j < n; j++)
                    {
                        k[i, j] += rhoVec[r] * row[i] * row[j];
                    }
                }
            }

            return TryCholesky(k) ?? throw new InvalidOperationException("QP matrix is not positive definite.");
        }

        private static double[,]? TryCholesky(double[,] k)
        {
            int n = k.GetLength(0);
            var lo = new double[n, n];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j <= i; j++)
                {
                    double sum = k[i, j];
                    for (int t = 0; t < j; t++)
                    {
                        sum -= lo[i, t] * lo[j, t];
                    }

                    if (i == j)
                    {
                        if (sum <= 0)
                        {
                            return null;
                        }

                        lo[i, i] = Math.Sqrt(sum);
                    }
                    else
                    {
                        lo[i, j] = sum / lo[j, j];
                    }
                }
            }

            return lo;
        }

        private static void CholeskySolve(double[,] lo, double[] b, double[] x)
        {
            int n = b.Length;
            var tmp = new double[n];
            for (int i = 0; i < n; i++)
            {
                double s = b[i];
                for (int t = 0; t < i; t++)
                {
                    s -= lo[i, t] * tmp[t];
                }

                tmp[i] = s / lo[i, i];
            }

            for (int i = n - 1; i >= 0; i--)
            {
                double s = tmp[i];
                for (int t = i + 1; t < n; t++)
                {
                    s -= lo[t, i] * x[t];
                }

                x[i] = s / lo[i, i];
            }
        }

        private static void MultiplyRows(double[][] rows, double[] x, double[] result)
        {
            for (int i = 0; i < rows.Length; i++)
            {
                double s = 0;
                double[] r = rows[i];
                for (int j = 0; j < x.Length; j++)
                {
                    s += r[j] * x[j];
                }

                result[i] = s;
            }
        }

        private static void Multiply(double[,] p, double[] x, double[] result)
        {
            int n = x.Length;
            for (int i = 0; i < n; i++)
            {
                double s = 0;
                for (int j = 0; j < n; j++)
                {
                    s += p[i, j] * x[j];
                }

                result[i] = s;
            }
        }

        private static double Clip(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
