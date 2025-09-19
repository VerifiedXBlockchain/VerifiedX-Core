using System.Security.Cryptography;
using System.Numerics;
using System.Text;

namespace ReserveBlockCore.EllipticCurve
{
    public static class Ecdsa
    {

        public static Signature sign(string message, PrivateKey privateKey)
        {
            string hashMessage = sha256(message);
            BigInteger numberMessage = BinaryAscii.numberFromHex(hashMessage);
            CurveFp curve = privateKey.curve;

            while (true)
            {
                BigInteger randNum = Integer.randomBetween(BigInteger.One, curve.N - 1);
                Point randSignPoint = EcdsaMath.multiply(curve.G, randNum, curve.N, curve.A, curve.P);
                
                BigInteger r = Integer.modulo(randSignPoint.x, curve.N);
                if (r.IsZero) continue; // Retry if r = 0
                
                BigInteger s = Integer.modulo((numberMessage + r * privateKey.secret) * EcdsaMath.inv(randNum, curve.N), curve.N);
                if (s.IsZero) continue; // Retry if s = 0
                
                return new Signature(r, s); // Valid signature found
            }
        }

        public static bool verify(string message, Signature signature, PublicKey publicKey)
        {
            string hashMessage = sha256(message);
            BigInteger numberMessage = BinaryAscii.numberFromHex(hashMessage);
            CurveFp curve = publicKey.curve;
            
            // Validate that the public key point lies on the curve
            if (!curve.contains(publicKey.point)) 
            { 
                return false; 
            }
            
            BigInteger sigR = signature.r;
            BigInteger sigS = signature.s;

            if (sigR < 1 || sigR >= curve.N)
            {
                return false;
            }
            if (sigS < 1 || sigS >= curve.N)
            {
                return false;
            }

            BigInteger inv = EcdsaMath.inv(sigS, curve.N);

            Point u1 = EcdsaMath.multiply(
                curve.G,
                Integer.modulo((numberMessage * inv), curve.N),
                curve.N,
                curve.A,
                curve.P
            );
            Point u2 = EcdsaMath.multiply(
                publicKey.point,
                Integer.modulo((sigR * inv), curve.N),
                curve.N,
                curve.A,
                curve.P
            );
            Point v = EcdsaMath.add(
                u1,
                u2,
                curve.A,
                curve.P
            );
            if (v.isAtInfinity())
            {
                return false;
            }
            return Integer.modulo(v.x, curve.N) == sigR;
        }

        public static string sha256(string message)
        {
            byte[] bytes;

            using (SHA256 sha256Hash = SHA256.Create())
            {
                bytes = sha256Hash.ComputeHash(Encoding.UTF8.GetBytes(message));
            }

            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < bytes.Length; i++)
            {
                builder.Append(bytes[i].ToString("x2"));
            }

            return builder.ToString();
        }

    }
}
