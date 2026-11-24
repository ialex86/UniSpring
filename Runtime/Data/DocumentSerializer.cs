using System;

namespace UniSpring.Data
{
    /// <summary>
    /// Turns a stored object into bytes and back. The module does not choose a format: the application registers
    /// one implementation (JSON, protobuf, ...) as a bean. It receives the record of an
    /// <see cref="IRecordMapper{TDomain,TRecord}"/> when the type has one, otherwise the object itself.
    /// </summary>
    public interface IDocumentSerializer
    {
        byte[] Write(object record);

        object Read(Type recordType, byte[] bytes);
    }
}
