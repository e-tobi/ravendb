//-----------------------------------------------------------------------
// <copyright file="QueryWithPercentageSign.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

using System.Linq;
using FastTests;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Xunit;

namespace SlowTests.Bugs.Queries
{
    public class QueryWithPercentageSign : RavenTestBase
    {
        [Fact]
        public void CanQueryUsingPercentageSign()
        {
            using (var store = GetDocumentStore())
            {
                store.Maintenance.Send(new PutIndexesOperation(new IndexDefinition
                {
                    Name = "Tags/Count",
                    Maps = {"from tag in docs.Tags select new { tag.Name, tag.UserId }"}
                }));

                using (var session = store.OpenSession())
                {
                    var userId = "users/1";
                    var tag = "24%";
                    // ReSharper disable once ReturnValueOfPureMethodIsNotUsed
                    session.Query<TagCount>("Tags/Count").FirstOrDefault(x => x.Name == tag && x.UserId == userId);
                }
            }
        }

        private class TagCount
        {
            public string Name { get; set; }
            public string UserId { get; set; }
        }
    }
}
