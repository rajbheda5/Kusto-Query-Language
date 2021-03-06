# estimate_data_size()

Returns an estimated data size of the selected columns of the tabular expression.

<!-- csl -->
```
estimate_data_size(*)
estimate_data_size(Col1, Col2, Col3)
```

**Syntax**

`estimate_data_size(*)`

`estimate_data_size(`*col1*`, `*col2*`, `...`)`

**Arguments**

* *col1*, *col2*: Selection of column references in the source tabular expression that are used for data size estimation. To include all columns, use `*` (asterisk) syntax.

**Returns**

* The estimated data size of the record size. Estimation is based on data types and values lengths.

**Examples**

Calculating total data size using `estimated_data_size()`:

<!-- csl: https://help.kusto.windows.net/Samples -->
```
range x from 1 to 10 step 1                    // x (long) is 8 
| extend Text = '1234567890'                   // Text length is 10  
| summarize Total=sum(estimate_data_size(*))   // (8+10)x10 = 180
```

|Total|
|---|
|180|
