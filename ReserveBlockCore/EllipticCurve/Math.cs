using System.Numerics;

namespace ReserveBlockCore.EllipticCurve
{
    public static class EcdsaMath
    {

        /// <summary>
        /// Computes scalar multiplication of an elliptic curve point using Jacobian coordinates.
        /// </summary>
        /// <param name="p">Point on the curve to multiply.</param>
        /// <param name="n">Scalar multiplier.</param>
        /// <param name="N">Order of the elliptic curve.</param>
        /// <param name="A">Coefficient of the first-order term of the equation Y^2 = X^3 + A*X + B (mod p).</param>
        /// <param name="P">Prime number in the modulus of the equation Y^2 = X^3 + A*X + B (mod p).</param>
        /// <returns>The resulting point n*p.</returns>
        public static Point multiply(Point p, BigInteger n, BigInteger N, BigInteger A, BigInteger P)
        {

            return fromJacobian(
                jacobianMultiply(
                    toJacobian(p),
                    n,
                    N,
                    A,
                    P
                ),
                P
            );
        }

        /// <summary>
        /// Adds two points on an elliptic curve using Jacobian coordinates.
        /// </summary>
        /// <param name="p">First point to add.</param>
        /// <param name="q">Second point to add.</param>
        /// <param name="A">Coefficient of the first-order term of the equation Y^2 = X^3 + A*X + B (mod p).</param>
        /// <param name="P">Prime number in the modulus of the equation Y^2 = X^3 + A*X + B (mod p).</param>
        /// <returns>Point that represents the sum of the first and second point.</returns>
        public static Point add(Point p, Point q, BigInteger A, BigInteger P)
        {

            return fromJacobian(
                jacobianAdd(
                    toJacobian(p),
                    toJacobian(q),
                    A,
                    P
                ),
                P
            );
        }

        /// <summary>
        /// Computes the modular multiplicative inverse using the Extended Euclidean Algorithm.
        /// This represents 'division' in elliptic curve operations.
        /// </summary>
        /// <param name="x">Value to find the inverse of.</param>
        /// <param name="n">Modulus for the inverse operation.</param>
        /// <returns>The modular multiplicative inverse of x modulo n.</returns>
        public static BigInteger inv(BigInteger x, BigInteger n)
        {

            if (x.IsZero)
            {
                return 0;
            }

            BigInteger lm = BigInteger.One;
            BigInteger hm = BigInteger.Zero;
            BigInteger low = Integer.modulo(x, n);
            BigInteger high = n;
            BigInteger r, nm, newLow;

            while (low > 1)
            {
                r = high / low;

                nm = hm - (lm * r);
                newLow = high - (low * r);

                high = low;
                hm = lm;
                low = newLow;
                lm = nm;
            }

            return Integer.modulo(lm, n);

        }

        /// <summary>
        /// Converts a point from affine coordinates to Jacobian coordinates.
        /// </summary>
        /// <param name="p">Point in affine coordinates to convert.</param>
        /// <returns>Point in Jacobian coordinates.</returns>
        private static Point toJacobian(Point p)
        {

            return new Point(p.x, p.y, 1);
        }

        /// <summary>
        /// Converts a point from Jacobian coordinates back to affine coordinates.
        /// </summary>
        /// <param name="p">Point in Jacobian coordinates to convert.</param>
        /// <param name="P">Prime number in the modulus of the equation Y^2 = X^3 + A*X + B (mod p).</param>
        /// <returns>Point in affine coordinates.</returns>
        private static Point fromJacobian(Point p, BigInteger P)
        {

            BigInteger z = inv(p.z, P);

            return new Point(
                Integer.modulo(p.x * BigInteger.Pow(z, 2), P),
                Integer.modulo(p.y * BigInteger.Pow(z, 3), P)
            );
        }

        /// <summary>
        /// Doubles a point on an elliptic curve using Jacobian coordinates.
        /// </summary>
        /// <param name="p">Point to double.</param>
        /// <param name="A">Coefficient of the first-order term of the equation Y^2 = X^3 + A*X + B (mod p).</param>
        /// <param name="P">Prime number in the modulus of the equation Y^2 = X^3 + A*X + B (mod p).</param>
        /// <returns>Point that represents 2*p.</returns>
        private static Point jacobianDouble(Point p, BigInteger A, BigInteger P)
        {

            if (p.y.IsZero)
            {
                return new Point(
                    BigInteger.Zero,
                    BigInteger.Zero,
                    BigInteger.Zero
                );
            }

            BigInteger ysq = Integer.modulo(
                BigInteger.Pow(p.y, 2),
                P
            );
            BigInteger S = Integer.modulo(
                4 * p.x * ysq,
                P
            );
            BigInteger M = Integer.modulo(
                3 * BigInteger.Pow(p.x, 2) + A * BigInteger.Pow(p.z, 4),
                P
            );

            BigInteger nx = Integer.modulo(
                BigInteger.Pow(M, 2) - 2 * S,
                P
            );
            BigInteger ny = Integer.modulo(
                M * (S - nx) - 8 * BigInteger.Pow(ysq, 2),
                P
            );
            BigInteger nz = Integer.modulo(
                2 * p.y * p.z,
                P
            );

            return new Point(
                nx,
                ny,
                nz
            );
        }

        /// <summary>
        /// Adds two points on an elliptic curve using Jacobian coordinates.
        /// </summary>
        /// <param name="p">First point to add.</param>
        /// <param name="q">Second point to add.</param>
        /// <param name="A">Coefficient of the first-order term of the equation Y^2 = X^3 + A*X + B (mod p).</param>
        /// <param name="P">Prime number in the modulus of the equation Y^2 = X^3 + A*X + B (mod p).</param>
        /// <returns>Point that represents the sum of the first and second point.</returns>
        private static Point jacobianAdd(Point p, Point q, BigInteger A, BigInteger P)
        {

            if (p.y.IsZero)
            {
                return q;
            }
            if (q.y.IsZero)
            {
                return p;
            }

            BigInteger U1 = Integer.modulo(
                p.x * BigInteger.Pow(q.z, 2),
                P
            );
            BigInteger U2 = Integer.modulo(
                q.x * BigInteger.Pow(p.z, 2),
                P
            );
            BigInteger S1 = Integer.modulo(
                p.y * BigInteger.Pow(q.z, 3),
                P
            );
            BigInteger S2 = Integer.modulo(
                q.y * BigInteger.Pow(p.z, 3),
                P
            );

            if (U1 == U2)
            {
                if (S1 != S2)
                {
                    return new Point(BigInteger.Zero, BigInteger.Zero, BigInteger.One);
                }
                return jacobianDouble(p, A, P);
            }

            BigInteger H = U2 - U1;
            BigInteger R = S2 - S1;
            BigInteger H2 = Integer.modulo(H * H, P);
            BigInteger H3 = Integer.modulo(H * H2, P);
            BigInteger U1H2 = Integer.modulo(U1 * H2, P);
            BigInteger nx = Integer.modulo(
                BigInteger.Pow(R, 2) - H3 - 2 * U1H2,
                P
            );
            BigInteger ny = Integer.modulo(
                R * (U1H2 - nx) - S1 * H3,
                P
            );
            BigInteger nz = Integer.modulo(
                H * p.z * q.z,
                P
            );

            return new Point(
                nx,
                ny,
                nz
            );
        }

        /// <summary>
        /// Performs scalar multiplication of an elliptic curve point using Jacobian coordinates.
        /// </summary>
        /// <param name="p">Point to multiply.</param>
        /// <param name="n">Scalar multiplier.</param>
        /// <param name="N">Order of the elliptic curve.</param>
        /// <param name="A">Coefficient of the first-order term of the equation Y^2 = X^3 + A*X + B (mod p).</param>
        /// <param name="P">Prime number in the modulus of the equation Y^2 = X^3 + A*X + B (mod p).</param>
        /// <returns>The resulting point n*p.</returns>
        private static Point jacobianMultiply(Point p, BigInteger n, BigInteger N, BigInteger A, BigInteger P)
        {

            if (p.y.IsZero | n.IsZero)
            {
                return new Point(
                    BigInteger.Zero,
                    BigInteger.Zero,
                    BigInteger.One
                );
            }

            if (n.IsOne)
            {
                return p;
            }

            if (n < 0 | n >= N)
            {
                return jacobianMultiply(
                    p,
                    Integer.modulo(n, N),
                    N,
                    A,
                    P
                );
            }

            if (Integer.modulo(n, 2).IsZero)
            {
                return jacobianDouble(
                    jacobianMultiply(
                        p,
                        n / 2,
                        N,
                        A,
                        P
                    ),
                    A,
                    P
                );
            }

            // (n % 2) == 1:
            return jacobianAdd(
                jacobianDouble(
                    jacobianMultiply(
                        p,
                        n / 2,
                        N,
                        A,
                        P
                    ),
                    A,
                    P
                ),
                p,
                A,
                P
            );

        }

    }

}
