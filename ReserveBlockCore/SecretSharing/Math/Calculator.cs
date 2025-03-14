﻿namespace ReserveBlockCore.SecretSharing.Math
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Reflection;

    /// <summary>
    /// This class represents the calculator strategy pattern to decouple Shamir's Secret Sharing
    /// implementation from the concrete numeric data type like <see cref="System.Numerics.BigInteger"/>.
    /// </summary>
    public abstract class Calculator
    {
        /// <summary>
        /// Saves a dictionary of number data types derived from the <see cref="Calculator{TNumber}"/> class.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security", "CA2104:DoNotDeclareReadOnlyMutableReferenceTypes")]
        private static readonly IReadOnlyDictionary<Type, Type> ChildTypes = new ReadOnlyDictionary<Type, Type>(GetDerivedNumberTypes());

        /// <summary>
        /// Saves a dictionary of constructors of number data types derived from the <see cref="Calculator{TNumber}"/> class.
        /// </summary>
        private static readonly IReadOnlyDictionary<Type, Func<byte[], Calculator>> ChildBaseCtors = new ReadOnlyDictionary<Type, Func<byte[], Calculator>>(GetDerivedCtors<byte[], Calculator>());

        /// <summary>
        /// Gets the number of elements of the byte representation of the <see cref="Calculator"/> object.
        /// </summary>
        public abstract int ByteCount { get; }

        /// <summary>
        /// Gets the byte representation of the <see cref="Calculator"/> object.
        /// </summary>
        public abstract IEnumerable<byte> ByteRepresentation { get; }

        /// <summary>
        /// Gets a value indicating whether or not the current <see cref="Calculator"/> object is zero.
        /// </summary>
        public abstract bool IsZero { get; }

        /// <summary>
        /// Gets a value indicating whether or not the current <see cref="Calculator"/> object is one.
        /// </summary>
        public abstract bool IsOne { get; }

        /// <summary>
        /// Gets a value indicating whether or not the current <see cref="Calculator"/> object is an even number.
        /// </summary>
        public abstract bool IsEven { get; }

        /// <summary>
        /// Gets a number that indicates the sign (negative, positive, or zero) of the current <see cref="Calculator"/> object.
        /// </summary>
        public abstract int Sign { get; }

        /// <summary>
        /// Converts the current <see cref="Calculator{TNumber}"/> instance to a <see cref="Int32"/>.
        /// </summary>
        /// <returns>A value of the <see cref="Int32"/> type.</returns>
        /// <exception cref="T:System.OverflowException">Unable to convert the current instance of <see cref="Calculator{TNumber}"/> class to <see cref="Int32"/>.</exception>
        public abstract int ToInt32();

        /// <summary>
        /// Creates a new instance derived from the <see cref="Calculator"/> class.
        /// </summary>
        /// <param name="data">byte array representation of the <paramref name="numberType"/></param>
        /// <param name="numberType">Type of number</param>
        /// <returns></returns>
        public static Calculator Create(byte[] data, Type numberType) => ChildBaseCtors[numberType](data);

        /// <summary>
        /// Returns a <see cref="System.String"/> that represents the current <see cref="Calculator"/>.
        /// </summary>
        /// <returns>The <see cref="System.String"/> representation of this <see cref="Calculator"/> object</returns>
        public abstract override string ToString();

        /// <summary>
        /// Returns a dictionary of constructors of number data types derived from the <see cref="Calculator"/> class.
        /// </summary>
        /// <returns></returns>
        protected static Dictionary<Type, Func<TParameter, TCalculator>> GetDerivedCtors<TParameter, TCalculator>() where TCalculator : Calculator
        {
            var res = new Dictionary<Type, Func<TParameter, TCalculator>>(ChildTypes.Count);
            var paramType = typeof(TParameter);
            var parameterExpression = Expression.Parameter(paramType);
            foreach (var childType in ChildTypes)
            {
                var ctorInfo = childType.Value.GetConstructor(new[] { paramType });
                if (ctorInfo == null)
                {
                    continue;
                }

                var ctor = Expression.Lambda<Func<TParameter, TCalculator>>(Expression.New(ctorInfo, parameterExpression), parameterExpression)
                    .Compile();
                res.Add(childType.Key, ctor);
            }

            return res;
        }

        /// <summary>
        /// Returns a dictionary of number data types derived from the <see cref="Calculator"/> class.
        /// </summary>
        /// <returns></returns>
        /// <remarks>The key represents the integer data type of the derived calculator. The value represents the type of derived calculator.</remarks>
        private static Dictionary<Type, Type> GetDerivedNumberTypes()
        {
            var asm = Assembly.GetAssembly(typeof(Calculator));
            var listOfClasses = asm?.GetTypes().Where(x => x.IsSubclassOf(typeof(Calculator)) && !x.IsGenericType);
            return listOfClasses?.ToDictionary(x => x.BaseType?.GetGenericArguments()[0]) ?? new Dictionary<Type, Type>();
        }
    }
}
