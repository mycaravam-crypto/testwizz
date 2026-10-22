# Test plan: `public int Divide(int dividend, int divisor)`

Source: `src/Shop/Calculator.cs` · generator: deterministic

| ID | Category | Scenario | Expected | Evidence | Review |
|----|----------|----------|----------|----------|--------|
| S1 | happy-path | Valid input<br>Inputs for which none of the guard conditions hold | Returns dividend / divisor | E1, E6 | needs review; maybe covered by CalculatorTests.Divide_ReturnsQuotient |
| S2 | exception | Throws DivideByZeroException when divisor == 0<br>divisor == 0 | Throws DivideByZeroException | E2, E3, E5 | ok |
| S3 | boundary | Boundary of divisor == 0<br>divisor = 0 (condition true) and divisor = 1 (condition false) | 0: throws DivideByZeroException; 1: passes this check | E3, E4 | needs review |

## Evidence

- **E1** doc: `summary: Divides <paramref name="dividend"/> by <paramref name="divisor"/>.`
- **E2** doc: `exception DivideByZeroException: When <paramref name="divisor"/> is zero.`
- **E3** guard (line 11): `if (divisor == 0)`
- **E4** comparison (line 11): `divisor == 0`
- **E5** throw (line 13): `throw new DivideByZeroException("Divisor must not be zero.");`
- **E6** return (line 16): `return dividend / divisor;`
- **E7** related-test: `CalculatorTests.Divide_ReturnsQuotient (tests/Shop.Tests/CalculatorTests.cs:16)`
