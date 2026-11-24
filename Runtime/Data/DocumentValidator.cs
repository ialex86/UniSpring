using System;
using System.Collections.Generic;

namespace UniSpring.Data
{
    /// <summary>Implemented through <see cref="IDocumentValidator{T}"/>; lets the module find the validators.</summary>
    public interface IDocumentValidator
    {
    }

    /// <summary>
    /// Checks an object of <typeparamref name="T"/> on its way in and out of the store. Register one (or several)
    /// as beans. An object with errors is not saved (<see cref="DocumentValidationException"/>); a stored one that
    /// fails the check is logged and read as missing.
    /// </summary>
    public interface IDocumentValidator<T> : IDocumentValidator
        where T : class
    {
        /// <param name="id">The id the object is stored under (the type name for a single document).</param>
        void Validate(string id, T value, ValidationErrors errors);
    }

    /// <summary>The problems validators found in one object.</summary>
    public sealed class ValidationErrors
    {
        private readonly List<string> m_Messages = new List<string>();

        public int Count => m_Messages.Count;

        public IReadOnlyList<string> Messages => m_Messages;

        public void Add(string message)
        {
            if (!string.IsNullOrEmpty(message))
                m_Messages.Add(message);
        }

        public override string ToString() => string.Join(" ", m_Messages);
    }

    /// <summary>A validator refused an object that was about to be saved.</summary>
    public sealed class DocumentValidationException : Exception
    {
        public DocumentValidationException(string document, ValidationErrors errors)
            : base(document + " is invalid and was not saved: " + errors)
        {
            Errors = errors;
        }

        public ValidationErrors Errors { get; }
    }
}
