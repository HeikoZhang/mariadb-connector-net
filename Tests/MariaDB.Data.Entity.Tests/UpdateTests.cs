// This program is free software; you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as published
// by the Free Software Foundation; version 3 of the License.
//
// This program is distributed in the hope that it will be useful, but
// WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY
// or FITNESS FOR A PARTICULAR PURPOSE. See the GNU Lesser General Public License
// for more details.
//
// You should have received a copy of the GNU Lesser General Public License along
// with this program; if not, write to the Free Software Foundation, Inc.,
// 51 Franklin St, Fifth Floor, Boston, MA 02110-1301  USA

using System;
using System.Data;
using System.Threading;
using MariaDB.Data.MySqlClient;
using NUnit.Framework;
using MariaDB.Data.MySqlClient.Tests;
using System.Data.EntityClient;
using System.Data.Common;
using System.Data.Objects;

namespace MariaDB.Data.Entity.Tests
{
    [TestFixture]
    public class UpdateTests : BaseEdmTest
    {
       [Test]
       public void UpdateAllRows()
       {
           MySqlCommand cmd = new MySqlCommand("SELECT COUNT(*) FROM toys", conn);
           object count = cmd.ExecuteScalar();

           using (testEntities context = new testEntities())
           {
               foreach (Toy t in context.Toys)
                   t.Name = "Top";
               context.SaveChanges();
           }

           cmd.CommandText = "SELECT COUNT(*) FROM Toys WHERE name='Top'";
           object newCount = cmd.ExecuteScalar();
           Assert.AreEqual(count, newCount);
       }
    }
}