# Columns

Every table in Kusto, and every tabular data stream, is a rectangular grid
of columns and rows. Every column in the table has a name (which must
follow the rules of [entity names](./entity-names.md)) and a specific
[scalar data type](../scalar-data-types/index.md). The columns of a table
or a tabular data stream are ordered, so a column also has a specific position
in the table's collection of columns.

In queries, columns are generally references by name only. They can only appear
in expressions, and the query operator under which the expression appears
determines the table or tabular data stream, so the column's name need not be
further scoped. For example, in the following query we have an unnamed tabular
data stream (defined through the [datatable operator](../datatableoperator.md)
that has a single column, `c`. The tabular data stream is then filtered by a predicate on
the value of that column, producing a new unnamed tabular data stream with the
same columns but fewer rows. The [as operator](../asoperator.md) then names
the tabular data stream and its value is returned as the results of the query.
Note in particular how the column `c` is referenced by name without a need to
reference its container (indeed, that container has no name):

<!-- csl -->
```
datatable (c:int) [int(-1), 0, 1, 2, 3]
| where c*c >= 2
| as Result
```

> As is often common in the relational databases world,
  rows are sometimes called **records** and columns are sometimes called
  **attributes**.

Details on managing columns can be found under [managing columns](../../management/columns.md).
