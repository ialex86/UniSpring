using System;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UniSpring.Transactions;
using UnityEngine;
using UnityEngine.TestTools;

namespace UniSpring.Data.Tests
{
    /// <summary>Repositories made from interfaces: base methods, queries by method name, mappers and validators.</summary>
    [TestFixture]
    public sealed class RepositoryTests
    {
        private DocumentData m_Data;
        private ICarRepository m_Cars;

        [SetUp]
        public void SetUp()
        {
            m_Data = DocumentData.InMemory(new JsonTestSerializer())
                .With(new WalletMapper())
                .With(new ParkedCarMapper())
                .With(new ParkedCarValidator());
            m_Cars = m_Data.Repository<ICarRepository>();
        }

        [Test]
        public void An_Object_Is_Saved_Found_Listed_And_Deleted_By_Its_Id()
        {
            m_Cars.Save("car-2", new ParkedCar("alex", "Coupe", 10));
            m_Cars.Save("car-1", new ParkedCar("sam", "Truck", 20));

            Assert.AreEqual("Coupe", m_Cars.Find("car-2").Model);
            Assert.IsTrue(m_Cars.Exists("car-1"));
            Assert.IsNull(m_Cars.Find("car-9"), "an id nothing is stored under");
            Assert.AreEqual(new[] { "car-1", "car-2" }, m_Cars.Ids());
            Assert.AreEqual(2, m_Cars.All().Count);

            m_Cars.Delete("car-1");

            Assert.IsFalse(m_Cars.Exists("car-1"));
            Assert.AreEqual(new[] { "car-2" }, m_Cars.Ids());
        }

        [Test]
        public void Query_Methods_Are_Implemented_From_Their_Names()
        {
            m_Cars.Save("car-1", new ParkedCar("alex", "Coupe", 10));
            m_Cars.Save("car-2", new ParkedCar("alex", "Truck", 20));
            m_Cars.Save("car-3", new ParkedCar("sam", "Coupe", 30));

            Assert.AreEqual(2, m_Cars.FindByOwner("alex").Count);
            Assert.AreEqual(1, m_Cars.FindByOwnerAndModel("alex", "Truck").Count);
            Assert.AreEqual(20, m_Cars.FindByOwnerAndModel("alex", "Truck")[0].Mileage);
            Assert.AreEqual("Truck", m_Cars.FindByModel("Truck").Model, "a single result is the first match");
            Assert.IsNull(m_Cars.FindByModel("Van"));
            Assert.AreEqual(1, m_Cars.CountByOwner("sam"));
            Assert.IsTrue(m_Cars.ExistsByModel("Coupe"));
            Assert.IsFalse(m_Cars.ExistsByModel("Van"));

            Assert.AreEqual(2, m_Cars.DeleteByOwner("alex"));
            Assert.AreEqual(new[] { "car-3" }, m_Cars.Ids());
        }

        [Test]
        public void The_Same_Repository_Is_Handed_Out_Every_Time()
        {
            Assert.AreSame(m_Cars, m_Data.Repository<ICarRepository>());
        }

        [Test]
        public void A_Single_Document_Is_Loaded_Saved_And_Deleted()
        {
            var wallet = m_Data.Repository<IWalletDocument>();

            Assert.IsNull(wallet.Load());
            Assert.IsFalse(wallet.Exists());

            wallet.Save(new Wallet(40));

            Assert.IsTrue(wallet.Exists());
            Assert.AreEqual(40, wallet.Load().Balance);

            wallet.Delete();

            Assert.IsNull(wallet.Load());
        }

        [Test]
        public void The_Mapper_Decides_What_Is_Stored()
        {
            m_Cars.Save("car-1", new ParkedCar("alex", "Coupe", 10));

            var stored = Encoding.UTF8.GetString(m_Data.Store.Read("ParkedCar", "car-1").Bytes);

            StringAssert.Contains("\"owner\":\"alex\"", stored, "the record's fields, not the domain's properties");
        }

        [Test]
        public void A_Type_Without_A_Mapper_Is_Stored_As_It_Is()
        {
            var note = m_Data.Repository<INoteDocument>();

            note.Save(new Note { text = "hello" });

            Assert.AreEqual("hello", note.Load().text);
        }

        [Test]
        public void An_Invalid_Object_Is_Not_Saved()
        {
            var error = Assert.Throws<DocumentValidationException>(
                () => m_Cars.Save("car-1", new ParkedCar(string.Empty, "Coupe", -5)));

            Assert.AreEqual(2, error.Errors.Count);
            Assert.IsFalse(m_Cars.Exists("car-1"));
        }

        [Test]
        public void A_Stored_Object_That_Fails_Validation_Is_Logged_And_Read_As_Missing()
        {
            m_Data.Store.Write("ParkedCar", "car-1", Encoding.UTF8.GetBytes("{\"owner\":\"\",\"model\":\"Coupe\",\"mileage\":1}"));
            LogAssert.Expect(LogType.Error, new Regex("Document ParkedCar/car-1 is invalid"));

            Assert.IsNull(m_Cars.Find("car-1"));
        }

        [Test]
        public void A_Document_The_Serializer_Cannot_Read_Is_Logged_And_Read_As_Missing()
        {
            m_Data.Store.Write("ParkedCar", "car-1", Encoding.UTF8.GetBytes("not json"));
            LogAssert.Expect(LogType.Exception, new Regex("JSON parse error"));
            LogAssert.Expect(LogType.Exception, new Regex("JSON parse error"));

            Assert.IsNull(m_Cars.Find("car-1"));
            Assert.AreEqual(0, m_Cars.All().Count);
        }

        [Test]
        public void A_Method_That_Cannot_Be_Implemented_Is_Refused_When_The_Repository_Is_Created()
        {
            StringAssert.Contains("Colour", Assert.Throws<InvalidOperationException>(
                () => m_Data.Repository<IBadNameRepository>()).Message);
            StringAssert.Contains("FindBy, CountBy", Assert.Throws<InvalidOperationException>(
                () => m_Data.Repository<IBadVerbRepository>()).Message);
            StringAssert.Contains("mileage", Assert.Throws<InvalidOperationException>(
                () => m_Data.Repository<IBadParameterRepository>()).Message);
            StringAssert.Contains("[Repository]", Assert.Throws<InvalidOperationException>(
                () => m_Data.Repository<INotMarked>()).Message);
        }

        [Test]
        public void Writes_Of_A_Transaction_Land_Together_Or_Not_At_All()
        {
            var wallet = m_Data.Repository<IWalletDocument>();
            var transactions = m_Data.Transactions;

            transactions.Run(() =>
            {
                wallet.Save(new Wallet(60));
                m_Cars.Save("car-1", new ParkedCar("alex", "Coupe", 0));
                Assert.AreEqual(60, wallet.Load().Balance, "the transaction reads its own writes");
            });
            Assert.AreEqual(60, wallet.Load().Balance);
            Assert.IsTrue(m_Cars.Exists("car-1"));

            Assert.Throws<InvalidOperationException>(() => transactions.Run(() =>
            {
                wallet.Save(new Wallet(0));
                m_Cars.Save("car-2", new ParkedCar("alex", "Truck", 0));
                throw new InvalidOperationException("the purchase failed");
            }, true));
            Assert.AreEqual(60, wallet.Load().Balance, "rolled back");
            Assert.IsFalse(m_Cars.Exists("car-2"), "rolled back");
        }
    }
}
