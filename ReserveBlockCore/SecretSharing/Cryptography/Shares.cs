﻿namespace ReserveBlockCore.SecretSharing.Cryptography
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Linq;
    using System.Text;
    using System.Threading;

    /// <summary>
    /// Represents a set of shares
    /// </summary>
    /// <typeparam name="TNumber">Numeric data type (An integer type)</typeparam>
    [Serializable]
    public sealed class Shares<TNumber> : ICollection<FinitePoint<TNumber>>, ICollection
    {
        /// <summary>
        /// Saves a collection of shares.
        /// </summary>
        private readonly Collection<FinitePoint<TNumber>> shareList;

        /// <summary>
        /// Saves an object that can be used to synchronize access to the <see cref="Shares{TNumber}"/>
        /// </summary>
        [NonSerialized]
        private object syncRoot;

        /// <summary>
        /// Initializes a new instance of the <see cref="Shares{TNumber}"/> class.
        /// </summary>
        /// <param name="shares">A list of <see cref="FinitePoint{TNumber}"/> objects.</param>
        /// <exception cref="ArgumentNullException"><paramref name="shares"/> is <see langword="null"/>.</exception>
        private Shares(IList<FinitePoint<TNumber>> shares)
        {
            _ = shares ?? throw new ArgumentNullException(nameof(shares));
            this.shareList = shares as Collection<FinitePoint<TNumber>> ?? new Collection<FinitePoint<TNumber>>(shares);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Shares{TNumber}"/> class.
        /// </summary>
        /// <param name="secret">The original secret which was split into <paramref name="shares"/>.</param>
        /// <param name="shares">A list of <see cref="FinitePoint{TNumber}"/> objects.</param>
        /// <exception cref="ArgumentNullException"><paramref name="secret"/> or <paramref name="shares"/> is <see langword="null"/>.</exception>
        internal Shares(Secret<TNumber> secret, IList<FinitePoint<TNumber>> shares)
        {
            this.OriginalSecret = secret;
            _ = shares ?? throw new ArgumentNullException(nameof(shares));
            this.shareList = shares as Collection<FinitePoint<TNumber>> ?? new Collection<FinitePoint<TNumber>>(shares);
        }

        /// <summary>
        /// Gets the original secret
        /// </summary>
        public Secret<TNumber>? OriginalSecret { get; }

        /// <summary>
        /// Gets the <see cref="FinitePoint{TNumber}"/> associated with the specified index.
        /// </summary>
        /// <param name="i">The index of the <see cref="FinitePoint{TNumber}"/> to get.</param>
        /// <returns>Returns a share (shared secret) represented by a <see cref="FinitePoint{TNumber}"/>.</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Naming", "CA1704:IdentifiersShouldBeSpelledCorrectly", MessageId = "i")]
        public FinitePoint<TNumber> this[int i] => this.shareList[i];

        /// <summary>
        /// Gets a value indicating whether or not the original secret is available.
        /// </summary>
        public bool OriginalSecretExists => this.OriginalSecret != null;

        /// <summary>
        /// Casts a <see cref="Shares{TNumber}"/> object to a array of <see cref="string"/>s.
        /// </summary>
        /// <param name="shares">A <see cref="Shares{TNumber}"/> object.</param>
        public static implicit operator string[](Shares<TNumber> shares) => shares?.Select(s => s.ToString()).ToArray();

        /// <summary>
        ///  Casts a <see cref="Shares{TNumber}"/> object to a <see cref="string"/> object.
        /// </summary>
        /// <param name="shares">A <see cref="Shares{TNumber}"/> object.</param>
        public static implicit operator string(Shares<TNumber> shares) => shares?.ToString();

        /// <summary>
        /// Casts a <see cref="string"/> object to a <see cref="Shares{TNumber}"/> object.
        /// </summary>
        /// <param name="s">A <see cref="string"/> object representing two or more finite points separated by newline.</param>
        public static implicit operator Shares<TNumber>(string s)
        {
            var points = s
                .Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => new FinitePoint<TNumber>(line))
                .ToArray();
            return new Shares<TNumber>(points);
        }

        /// <summary>
        /// Casts an array of <see cref="string"/> (representing two or more finite points) to a <see cref="Shares{TNumber}"/> object.
        /// </summary>
        /// <param name="s">An array of <see cref="string"/> representing two or more finite points.</param>
        public static implicit operator Shares<TNumber>(string[] s)
        {
            var points = s
                .Select(line => new FinitePoint<TNumber>(line))
                .ToArray();
            return new Shares<TNumber>(points);
        }

        /// <summary>
        /// Casts a <see cref="Shares{TNumber}"/> object to an array of <see cref="FinitePoint{TNumber}"/> items.
        /// </summary>
        /// <param name="shares">A <see cref="Shares{TNumber}"/> object.</param>
        public static explicit operator FinitePoint<TNumber>[](Shares<TNumber> shares) =>
            shares.Select(s => s).ToArray();

        /// <summary>
        /// Returns the string representation of the <see cref="Shares{TNumber}"/> instance.
        /// </summary>
        /// <returns>A human readable list of shares separated by newlines</returns>
        public override string ToString()
        {
            var stringBuilder = new StringBuilder();
            var shares = this.shareList as FinitePoint<TNumber>[] ?? this.shareList.ToArray();
            foreach (var share in shares)
            {
                stringBuilder.AppendLine(share.ToString());
            }

            return stringBuilder.ToString();
        }

        /// <summary>
        /// Returns an enumerator that iterates through a <see cref="Shares{TNumber}"/> collection.
        /// </summary>
        /// <returns>An enumerator that can be used to iterate through the <see cref="Shares{TNumber}"/> collection.</returns>
        IEnumerator<FinitePoint<TNumber>> IEnumerable<FinitePoint<TNumber>>.GetEnumerator() => this.GetEnumerator();

        /// <summary>
        /// Returns an enumerator that iterates through a <see cref="Shares{TNumber}"/> collection.
        /// </summary>
        /// <returns>An <see cref="IEnumerator"/> object that can be used to iterate through the <see cref="Shares{TNumber}"/> collection.</returns>
        IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();

        /// <summary>
        /// Returns an <see cref="SharesEnumerator{TNumber}"/> that iterates through the <see cref="Shares{TNumber}"/> collection.
        /// </summary>
        /// <returns>An <see cref="SharesEnumerator{TNumber}"/> that can be used to iterate through the <see cref="Shares{TNumber}"/> collection.</returns>
        public SharesEnumerator<TNumber> GetEnumerator() => new SharesEnumerator<TNumber>(this.shareList);

        /// <summary>
        /// Gets a value indicating whether the <see cref="Shares{TNumber}"/> collection is read-only.
        /// </summary>
        /// <remarks>Currently, this property always returns <see langword="true"/>.</remarks>
        public bool IsReadOnly => true;

        /// <summary>
        /// Gets the number of <see cref="FinitePoint{TNumber}"/> items contained in the <see cref="Shares{TNumber}"/> collection.
        /// </summary>
        public int Count => this.shareList.Count;

        /// <summary>
        /// Determines whether the <see cref="Shares{TNumber}"/> collection contains a specific <see cref="FinitePoint{TNumber}"/>.
        /// </summary>
        /// <param name="item">The <see cref="FinitePoint{TNumber}"/> to locate in the <see cref="Shares{TNumber}"/> collection.</param>
        /// <returns><see langword="true"/> if item is found in the <see cref="Shares{TNumber}"/> collection; otherwise, <see langword="false"/>.</returns>
        public bool Contains(FinitePoint<TNumber> item) => this.shareList.Any(share => share.Equals(item));

        /// <summary>
        /// Removes all items from the <see cref="Shares{TNumber}"/> collection.
        /// </summary>
        /// <remarks>This method is implemented. However this method does nothing as long as the property <see cref="IsReadOnly"/> is
        /// set to <see langword="true"/>.</remarks>
        /// <exception cref="NotSupportedException">The <see cref="Shares{TNumber}"/> collection is read-only.</exception>
        public void Clear()
        {
            if (this.IsReadOnly)
            {
                throw new NotSupportedException(string.Format(nameof(Shares<TNumber>)));
            }

            this.shareList.Clear();
        }

        /// <summary>
        /// Adds an <see cref="FinitePoint{TNumber}"/> to the <see cref="Shares{TNumber}"/> collection.
        /// </summary>
        /// <param name="item">The <see cref="FinitePoint{TNumber}"/> to add to the <see cref="Shares{TNumber}"/> collection.</param>
        /// <remarks>This method is implemented. However this method does nothing as long as the property <see cref="IsReadOnly"/> is
        /// set to <see langword="true"/>.</remarks>
        /// <exception cref="NotSupportedException">The <see cref="Shares{TNumber}"/> collection is read-only.</exception>
        public void Add(FinitePoint<TNumber> item)
        {
            if (this.IsReadOnly)
            {
                throw new NotSupportedException(string.Format(nameof(Shares<TNumber>)));
            }

            if (!this.Contains(item))
            {
                this.shareList.Add(item);
            }
        }

        /// <summary>
        /// Removes the first occurrence of a specific <see cref="FinitePoint{TNumber}"/> from the <see cref="Shares{TNumber}"/> collection.
        /// </summary>
        /// <param name="item">The <see cref="FinitePoint{TNumber}"/> to remove from the <see cref="Shares{TNumber}"/> collection.</param>
        /// <returns></returns>
        /// <remarks>This method is implemented. However this method does nothing as long as the property <see cref="IsReadOnly"/> is
        /// set to <see langword="true"/>.</remarks>
        /// <exception cref="NotSupportedException">The <see cref="Shares{TNumber}"/> collection is read-only.</exception>
        public bool Remove(FinitePoint<TNumber> item)
        {
            if (this.IsReadOnly)
            {
                throw new NotSupportedException(string.Format(nameof(Shares<TNumber>)));
            }

            return this.shareList.Remove(item);
        }

        /// <summary>
        /// Copies the items of the <see cref="Shares{TNumber}"/> collection to an <see cref="Array"/>, starting at a particular <see cref="Array"/> index.
        /// </summary>
        /// <param name="array">The one-dimensional <see cref="Array"/> that is the destination of the items copied from <see cref="Shares{TNumber}"/> collection.
        /// The  <see cref="Array"/> must have zero-based indexing.</param>
        /// <param name="index">The zero-based index in <paramref name="array"/> at which copying begins.</param>
        void ICollection.CopyTo(Array array, int index)
        {
            _ = array ?? throw new ArgumentNullException(nameof(array));
            switch (array)
            {
                case FinitePoint<TNumber>[] x:
                    this.CopyTo(x, index);
                    break;
                default:
                    throw new InvalidCastException(string.Format(nameof(array), array.GetType().GetElementType(), typeof(FinitePoint<TNumber>)));
            }
        }

        /// <summary>
        /// Copies the items of the <see cref="Shares{TNumber}"/> collection to an array of <see cref="FinitePoint{TNumber}"/> items,
        /// starting at a particular array index.
        /// </summary>
        /// <param name="array">The one-dimensional array of <see cref="FinitePoint{TNumber}"/> items that is the destination of the
        /// items copied from <see cref="Shares{TNumber}"/> collection.
        /// The array must have zero-based indexing.</param>
        /// <param name="arrayIndex">The zero-based index in <paramref name="array"/> at which copying begins.</param>
        public void CopyTo(FinitePoint<TNumber>[] array, int arrayIndex)
        {
            _ = array ?? throw new ArgumentNullException(nameof(array));
            if (arrayIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(arrayIndex));
            }

            if (this.Count > array.Length - arrayIndex + 1)
            {
                throw new ArgumentException(nameof(array));
            }

            for (int i = 0; i < this.shareList.Count; i++)
            {
                array[i + arrayIndex] = this.shareList[i];
            }
        }

        /// <summary>
        /// Gets an object that can be used to synchronize access to the <see cref="Shares{TNumber}"/> collection.
        /// </summary>
        object ICollection.SyncRoot
        {
            get
            {
                object newValue = new object();
                return (this.syncRoot ?? Interlocked.CompareExchange(ref this.syncRoot, newValue, null)) ?? newValue;
            }
        }

        /// <summary>
        /// Gets a value indicating whether access to the <see cref="Shares{TNumber}"/> collection is synchronized (thread safe).
        /// </summary>
        bool ICollection.IsSynchronized => false;
    }
}
