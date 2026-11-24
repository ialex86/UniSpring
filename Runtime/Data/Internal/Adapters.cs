using System;
using System.Collections.Generic;

namespace UniSpring.Data.Internal
{
    /// <summary>A typed mapper seen without its type arguments.</summary>
    internal abstract class RecordMapping
    {
        public abstract Type DomainType { get; }

        public abstract Type RecordType { get; }

        public abstract object ToRecord(object domain);

        public abstract object ToDomain(object record);

        public static IEnumerable<RecordMapping> Of(IRecordMapper mapper)
        {
            foreach (var face in mapper.GetType().GetInterfaces())
            {
                if (!face.IsGenericType || face.GetGenericTypeDefinition() != typeof(IRecordMapper<,>))
                    continue;
                var adapter = typeof(RecordMapping<,>).MakeGenericType(face.GetGenericArguments());
                yield return (RecordMapping)Activator.CreateInstance(adapter, mapper);
            }
        }
    }

    internal sealed class RecordMapping<TDomain, TRecord> : RecordMapping
        where TDomain : class
        where TRecord : class
    {
        private readonly IRecordMapper<TDomain, TRecord> m_Mapper;

        public RecordMapping(IRecordMapper<TDomain, TRecord> mapper)
        {
            m_Mapper = mapper;
        }

        public override Type DomainType => typeof(TDomain);

        public override Type RecordType => typeof(TRecord);

        public override object ToRecord(object domain) => m_Mapper.ToRecord((TDomain)domain);

        public override object ToDomain(object record) => m_Mapper.ToDomain((TRecord)record);
    }

    /// <summary>A typed validator seen without its type argument.</summary>
    internal abstract class DocumentValidation
    {
        public abstract Type DomainType { get; }

        public abstract void Validate(string id, object value, ValidationErrors errors);

        public static IEnumerable<DocumentValidation> Of(IDocumentValidator validator)
        {
            foreach (var face in validator.GetType().GetInterfaces())
            {
                if (!face.IsGenericType || face.GetGenericTypeDefinition() != typeof(IDocumentValidator<>))
                    continue;
                var adapter = typeof(DocumentValidation<>).MakeGenericType(face.GetGenericArguments());
                yield return (DocumentValidation)Activator.CreateInstance(adapter, validator);
            }
        }
    }

    internal sealed class DocumentValidation<T> : DocumentValidation
        where T : class
    {
        private readonly IDocumentValidator<T> m_Validator;

        public DocumentValidation(IDocumentValidator<T> validator)
        {
            m_Validator = validator;
        }

        public override Type DomainType => typeof(T);

        public override void Validate(string id, object value, ValidationErrors errors)
            => m_Validator.Validate(id, (T)value, errors);
    }
}
