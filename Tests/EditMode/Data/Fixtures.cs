using System;
using System.Collections.Generic;
using System.Text;
using UniSpring.DependencyInjection;
using UnityEngine;

namespace UniSpring.Data.Tests
{
    // ---------------------------------------------------------------- domain: knows nothing of storage

    public sealed class Wallet
    {
        public Wallet(long balance)
        {
            Balance = balance;
        }

        public long Balance { get; }
    }

    public sealed class ParkedCar
    {
        public ParkedCar(string owner, string model, int mileage)
        {
            Owner = owner;
            Model = model;
            Mileage = mileage;
        }

        public string Owner { get; }
        public string Model { get; }
        public int Mileage { get; }
    }

    // ---------------------------------------------------------------- stored shapes

    [Serializable]
    public sealed class WalletRecord
    {
        public long balance;
    }

    [Serializable]
    public sealed class WalletRecordV1
    {
        public int coins;
    }

    [Serializable]
    public sealed class ParkedCarRecord
    {
        public string owner;
        public string model;
        public int mileage;
    }

    /// <summary>Stored as it is: no mapper.</summary>
    [Serializable]
    public sealed class Note
    {
        public string text;
    }

    // ---------------------------------------------------------------- mappers and validators

    [Component]
    public sealed class WalletMapper : IRecordMapper<Wallet, WalletRecord>
    {
        public WalletRecord ToRecord(Wallet wallet) => new WalletRecord { balance = wallet.Balance };

        public Wallet ToDomain(WalletRecord record) => new Wallet(record.balance);
    }

    [Component]
    public sealed class ParkedCarMapper : IRecordMapper<ParkedCar, ParkedCarRecord>
    {
        public ParkedCarRecord ToRecord(ParkedCar car)
            => new ParkedCarRecord { owner = car.Owner, model = car.Model, mileage = car.Mileage };

        public ParkedCar ToDomain(ParkedCarRecord record)
            => new ParkedCar(record.owner, record.model, record.mileage);
    }

    [Component]
    public sealed class ParkedCarValidator : IDocumentValidator<ParkedCar>
    {
        public void Validate(string id, ParkedCar car, ValidationErrors errors)
        {
            if (string.IsNullOrEmpty(car.Owner))
                errors.Add("The car has no owner.");
            if (car.Mileage < 0)
                errors.Add("The mileage is negative.");
        }
    }

    // ---------------------------------------------------------------- repositories: interfaces only

    [Repository]
    public interface IWalletDocument : IDocument<Wallet>
    {
    }

    [Repository]
    public interface INoteDocument : IDocument<Note>
    {
    }

    [Repository]
    public interface ICarRepository : IRepository<ParkedCar>
    {
        IReadOnlyList<ParkedCar> FindByOwner(string owner);
        IReadOnlyList<ParkedCar> FindByOwnerAndModel(string owner, string model);
        ParkedCar FindByModel(string model);
        int CountByOwner(string owner);
        bool ExistsByModel(string model);
        int DeleteByOwner(string owner);
    }

    [Repository]
    public interface IBadNameRepository : IRepository<ParkedCar>
    {
        IReadOnlyList<ParkedCar> FindByColour(string colour);
    }

    [Repository]
    public interface IBadVerbRepository : IRepository<ParkedCar>
    {
        IReadOnlyList<ParkedCar> CarsOf(string owner);
    }

    [Repository]
    public interface IBadParameterRepository : IRepository<ParkedCar>
    {
        int CountByMileage(string mileage);
    }

    public interface INotMarked : IRepository<ParkedCar>
    {
    }

    // ---------------------------------------------------------------- serializer

    [Service]
    public sealed class JsonTestSerializer : IDocumentSerializer
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false);

        public byte[] Write(object record) => Utf8.GetBytes(JsonUtility.ToJson(record));

        public object Read(Type recordType, byte[] bytes) => JsonUtility.FromJson(Utf8.GetString(bytes), recordType);
    }
}
