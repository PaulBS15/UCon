using MyLogger;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;

namespace UCon {
	[SupportedOSPlatform("windows8.0")]

	public class UnitConverter {

		public static TokenList<Unit> SharedUnits { get; } = [];
		public static TokenList<Operator> Operators { get; } = [];

		protected static UnaryOperator Identity;
		protected static UnaryOperator Negation;

		protected static BinaryOperator Plus;
		protected static BinaryOperator Minus;
		protected static BinaryOperator Multiply;
		protected static BinaryOperator Power;
		internal static InterpretedUnits interpretedUnits = InterpretedUnits.Instance;


		static readonly string[] SciNumberFormats = [" + ", " +", "+ ", "+", " - ", " -", "- ", "-", " "];

		public TokenList<Unit> Units { get; } = SharedUnits.Branch();

		static UnitConverter sharedUnitConverter;

		public static object Calculate(string LeftSide, string RightSide = "", double? Value = null) {

			RightSide ??= "";
			double value = Value ??= 1.0;
			InterpretedUnit interpretedLeftSide;
			InterpretedUnit interpretedRightSide;

			// Convert temperature units if left side and right side are both a pure temperature unit, e.g. K, Rankine, °C, °F, etc.

			if (Value != null
							 && TemperatureUnits.TryGetValue(LeftSide, out LinearCoefficients LSCoeff)
							 && TemperatureUnits.TryGetValue(RightSide, out LinearCoefficients RSCoeff)) {
				double T1 = LSCoeff.A * (double)Value + LSCoeff.B;
				return (T1 - RSCoeff.B) / RSCoeff.A;
			}

			sharedUnitConverter ??= new UnitConverter();

			Logger.LogIfDebugging($"Leftside: {LeftSide}, Rightside {RightSide}, Value: {Value}");

			if (LeftSide == string.Empty && RightSide != string.Empty) {
				interpretedRightSide = sharedUnitConverter.InterpretUnit(RightSide);
				return Convert(interpretedRightSide, value, true);
			}
			interpretedLeftSide = sharedUnitConverter.InterpretUnit(LeftSide);
			if (RightSide != string.Empty) {
				interpretedRightSide = sharedUnitConverter.InterpretUnit(RightSide);
				return Convert(interpretedLeftSide, interpretedRightSide, Value);
			}
			else {
				return Convert(interpretedLeftSide, value);
				//return retv
			}
		}

		public static Unit GetBaseUnit(string UnitExpression) {
			sharedUnitConverter ??= new UnitConverter();
			if (UnitExpression == null) {
				return null;
			}
			else {
				return sharedUnitConverter.BaseUnit(UnitExpression.Trim());
			}
		}

		public Unit BaseUnit(string Exp) {

			bool isGauge = CheckIfPressure(Exp, out string strippedExp);

			try {
				RPNExpression = GenerateRPN(Tokenize(FormatExpression(Exp)));
			}
			catch (UnitNotDefinedException e) {

				//Could not convert unit, see if it is a gauge pressure unit

				try {
					if (isGauge)
						RPNExpression = GenerateRPN(Tokenize(FormatExpression(strippedExp)));
					else
						throw new InvalidParameterException($"Unable to process {strippedExp}");
				}
				catch (Exception) {
					throw e;
				}
			}
			return EvaluateRPN(RPNExpression);
		}

		char DecimalSeparator { get; } = '.';

		protected static char[] Delimiters { get; private set; }

		protected static double GaugePressure { get; } = 101325.0;

		private readonly static Dictionary<string, LinearCoefficients> TemperatureUnits = [];

		// private Unit u = new(0.0, [0, 0, 0, 0, 0, 0, 0]);

		public List<Token> RPNExpression { get; private set; }

		public UnitConverter() {
		}

		static UnitConverter() {
			SharedUnits.Add(Unit.LoadUnits("Conversion Factors.txt"));
			Unit.LoadPrefixes("Prefixes.txt");
			Unit.LoadSuffixes("GaugeStrings.txt");
			LoadTempUnits("TemperatureFactors.txt");

			Identity = new UnaryOperator("+", 8, x => x, false);
			Negation = new UnaryOperator("-", 10, x => -x, false) { IsRightAssociated = true };

			Plus = new BinaryOperator("+", 4, (x, y) => x + y, true);
			Minus = new BinaryOperator("-", 4, (x, y) => x - y, true);
			Multiply = new BinaryOperator("*", 7, (x, y) => x * y, false);
			Power = new BinaryOperator("^", 10, PowerFunc, false) { IsRightAssociated = true };
			Operators.Add(Plus,
							  Minus,
							  Multiply,
							  Power,
							  new BinaryOperator("/", 7, (x, y) => x / y, false));

			List<char> list = [ Punctuation.LeftParenthesis.Symbol[0],
									  Punctuation.RightParenthesis.Symbol[0],
									  Punctuation.Space.Symbol[0] ];

			foreach (Operator op in Operators) {
				if (!op.NumbersOnly) list.Add(op.Symbol[0]);
			}
			Delimiters = [.. list];

		}
		internal InterpretedUnit InterpretUnit(string UnitString) {

			if (interpretedUnits.TryGetValue(UnitString, out InterpretedUnit iu)) {
				return iu;
			}

			bool firstPassSuccess = true;
			Unit unit;

			bool isGauge = CheckIfPressure(UnitString, out string strippedUnit);

			try {
				RPNExpression = GenerateRPN(Tokenize(FormatExpression(UnitString)));
				unit = EvaluateRPN(RPNExpression);
			}
			catch (UnitNotDefinedException e) {

				//Could not convert unit, see if it is a gauge pressure unit

				firstPassSuccess = false;
				try {
					if (isGauge) {
						RPNExpression = GenerateRPN(Tokenize(FormatExpression(strippedUnit)));
						unit = EvaluateRPN(RPNExpression);
						if (!unit.IsPressureUnit) throw new InvalidPressureUnitException(UnitString);
					}
					else
						throw new InvalidParameterException($"Unable to process {UnitString}");
				}
				catch (Exception) {
					throw e;
				}
			}
			InterpretedUnit interpretedUnit = new(unit, !firstPassSuccess && isGauge);
			interpretedUnits.Add(UnitString, interpretedUnit);
			return interpretedUnit;
		}

		// Convert from Specified Unit to SI Unit since Right Side Unit is not specified

		internal static double Convert(InterpretedUnit InterpretedUnit, double Value, bool Invert = false) {

			if (!Invert)
				return InterpretedUnit.Unit.Factor*Value;
			else
				return Value/InterpretedUnit.Unit.Factor;
		}

		internal static double Convert(InterpretedUnit LeftUnit, InterpretedUnit RightUnit, double? Value) {

			double value = Value ?? 1.0;

			if (!LeftUnit.Unit.EquivalentTo(RightUnit.Unit)) {
				throw new BaseDimensionsDontMatchException(LeftUnit.Unit, RightUnit.Unit);
			}

			// If converting a gauge pressure unit, make sure the conversion is done correctly
			// Use conditional OR if both gauge or both absolute, gauge pressure conversion is not needed

			if (LeftUnit.IsGauge ^ RightUnit.IsGauge) {
				if (Value == null) {
					throw new MissingGaugePressureValueException();
				}
				else {
					value *= LeftUnit.Unit.Factor;
					if (LeftUnit.IsGauge) {  // Convert Left Side to absolute SI Unit if it is in gauge
						value += GaugePressure;
						if (value < 0) throw new InvalidGaugePressureValueException();
					}
					if (RightUnit.IsGauge) {
						value = (value - GaugePressure)/RightUnit.Unit.Factor;
					}
					else {
						value /= RightUnit.Unit.Factor;
					}
				}
				return value;
			}

			return value*(LeftUnit.Unit.Factor/RightUnit.Unit.Factor);
		}

		//public double Convert(string LeftSide, string RightSide = "", double? Value = null) {

		//   Unit leftSide = new Unit();
		//   Unit rightSide = new Unit();

		//   bool leftSideGauge = false;
		//   bool rightSideGauge = false;
		//   bool firstPassSuccess = true;

		//   string strippedLeftSide = "";
		//   string strippedRightSide = "";
		//   InterpretedUnit interpretedUnit;

		//   if (Value != null) {
		//      leftSideGauge = CheckIfPressure(LeftSide, out strippedLeftSide);
		//      rightSideGauge = (RightSide.Length == 0) ? false : CheckIfPressure(RightSide, out strippedRightSide);
		//   }

		//   //Get conversion factor to SI Units for the Left Side

		//   try {
		//      RPNExpression = GenerateRPN(Tokenize(FormatExpression(LeftSide)));
		//   }
		//   catch (UnitNotDefinedException e) {

		//      //Could not convert unit, see if it is a gauge pressure unit

		//      firstPassSuccess = false;
		//      try {
		//         if (leftSideGauge)
		//            RPNExpression = GenerateRPN(Tokenize(FormatExpression(strippedLeftSide)));
		//         else
		//            throw new InvalidParameterException($"Unable to process {strippedLeftSide}");
		//      }
		//      catch (Exception) {
		//         throw e;
		//      }
		//   }
		//   leftSide = EvaluateRPN(RPNExpression);
		//   interpretedUnit = new(EvaluateRPN(RPNExpression), !firstPassSuccess && leftSideGauge);

		//   //Left Side is gauge pressure - convert to absolute pressure

		//   if (!firstPassSuccess && leftSideGauge) {
		//      if (leftSide.IsPressureUnit) {
		//         leftSide.Factor = leftSide.Factor * (double)Value + GaugePressure;
		//         Value = 1.0;
		//         if (leftSide.Factor < 0)
		//            throw new InvalidGaugePressureValueException();
		//      }
		//      else throw new InvalidPressureUnitException(strippedLeftSide);
		//   }
		//   interpretedUnit = new(leftSide, !firstPassSuccess && leftSideGauge);
		//   interpretedUnits.Add(LeftSide, interpretedUnit);

		//   firstPassSuccess = false;
		//   if (RightSide.Length > 0) {
		//      try {
		//         RPNExpression = GenerateRPN(Tokenize(FormatExpression(RightSide)));
		//         rightSideGauge = false;
		//      }

		//      //Could not convert unit, see if it is a gauge pressure unit

		//      catch (UnitNotDefinedException e) {
		//         try {
		//            if (rightSideGauge)
		//               RPNExpression = GenerateRPN(Tokenize(FormatExpression(strippedRightSide)));
		//            else
		//               throw new InvalidParameterException($"Unable to process {strippedRightSide}");
		//         }
		//         catch (Exception) {
		//            throw e;
		//         }
		//      }
		//      rightSide = EvaluateRPN(RPNExpression);


		//      double pressureToGauge = 0;
		//      if (!firstPassSuccess && rightSideGauge) {
		//         if (rightSide.IsPressureUnit) {
		//            pressureToGauge = GaugePressure / rightSide.Factor;
		//         }
		//         else {
		//            throw new InvalidPressureUnitException(RightSide);
		//         }
		//      }

		//      if (!leftSide.EquivalentTo(rightSide)) throw new BaseDimensionsDontMatchException(leftSide, rightSide);
		//      u = leftSide / rightSide;
		//      interpretedUnit = new(rightSide, !firstPassSuccess && rightSideGauge);
		//      interpretedUnits.Add(RightSide, interpretedUnit);

		//      //foreach (double d in u.D) {
		//      //   if (Math.Abs(d / 1e-6) > 1.0) throw new BaseDimensionsDontMatchException(leftSide, rightSide);
		//      //}
		//      return (Value == null) ? u.Factor : u.Factor * (double)Value - pressureToGauge;
		//   }
		//   return (Value == null) ? leftSide.Factor : leftSide.Factor * (double)Value;
		//}

		static Unit EvaluateRPN(IEnumerable<Token> RPNExpression) {
			var stack = new Stack<Unit>(); // Contains operands
			if (DnaGlobals.DnaGlobal.Debug) {
				foreach (Token token in RPNExpression) {
					Logger.Log(token.ToString());
				}
			}
			// Analyse entire expression
			foreach (var token in RPNExpression) {
				// if it's operand then just push it to stack
				if (token is Unit unit)
					stack.Push(unit);
				else if (token is Number number) {
					stack.Push(number);
				}
				else if (token is IEvaluatable) {
					var eval = token as IEvaluatable;

					// No of values to Pop.
					var count = eval.NumOfParams;

					var arguments = new Unit[count];

					// Values are popped out in reverse order.
					for (var i = count - 1; i >= 0; --i)
						arguments[i] = stack.Pop();

					// Invoke IEvaluatable and push the result back to stack.
					stack.Push(eval.Invoke(arguments));
				}
			}

			// At end of analysis in stack should be only one operand (result)
			if (stack.Count > 1) {
				Logger.Log($"Stack count is greater than one in EvaluateRPN method");
				foreach (Unit u in stack) {
					Logger.LogIfDebugging(u.ToString());
				}
				throw new ArgumentException("Excess operand");
			}

			//If there is only one element in the stack, must multiply by 1 to incorporate scale factor;

			if (RPNExpression.Count<Token>() == 1) return Multiply.Invoke(new Unit[2] { new Number(1.0), stack.Pop() });
			return stack.Pop();
		}

		static string FormatExpression(string Expression) {

			string str = Expression.Trim();
			StringBuilder firstEdit = new(str);
			firstEdit.Replace('[', '(');
			firstEdit.Replace('{', '(');
			firstEdit.Replace(']', ')');
			firstEdit.Replace('}', ')');
			string secondString = firstEdit.ToString();
			Logger.LogIfDebugging($"In FormatExpression, after first edit, expression: {secondString}");

			StringBuilder secondEdit = new(str.Length);
			long parenthCheck = 0;
			bool hitWhiteSpace = false;

			foreach (char c in secondString) {
				switch (c) {
					case '(':
						parenthCheck++;
						break;
					case ')':
						parenthCheck--;
						break;
				}
				if (parenthCheck < 0) throw new FormatException("Parenthesis are out of order in " + Expression);

				// condense two or more spaces into one space

				if (char.IsWhiteSpace(c)) {
					if (hitWhiteSpace) continue;
					else hitWhiteSpace = true;
				}
				else
					hitWhiteSpace = false;

				secondEdit.Append(c);
			}
			if (parenthCheck != 0) throw new FormatException("Left and Right Parenthesis are not balanced");
			//retString.Replace(' ','*');
			secondEdit.Replace('δ', 'Δ').Replace('²', '2').Replace('³', '3').Replace('º', '°');
			secondEdit.Replace('×', '*').Replace('·', '*');
			secondEdit.Replace('÷', '/');
			secondEdit.Replace(")(", ")*(").Replace(") (", ")*(");
			//			return secondEdit.Replace(")(", ")*(").Replace(") (",")*(").ToString();
			Logger.LogIfDebugging($"In FormatExpression, after second edit, expression: {secondEdit}");
			return secondEdit.ToString();
		}

		static List<Token> GenerateRPN(IEnumerable<Token> Input) {
			var output = new List<Token>();
			var stack = new Stack<Token>();

			if (DnaGlobals.DnaGlobal.Debug) {
				Logger.Log($"Token stack at entrance to GenerateRPN method");
				foreach (Token t in Input) {
					Logger.Log(t.ToString());
				}
			}
			foreach (var token in Input) {
				// If it's a comma, pop items from list until we find another comma or left paranthesis
				if (token == Punctuation.Comma) {
					while (stack.Count > 0) {
						var peek = stack.Peek();

						if (peek == Punctuation.LeftParenthesis)
							break;

						if (peek == Punctuation.Comma) {
							stack.Pop();
							break;
						}

						output.Add(stack.Pop());
					}

					stack.Push(Punctuation.Comma);
				}

				// If it's a number or unit just put to list

				else if (token is Number || token is Unit)
					output.Add(token);

				// If its '(' push to stack

				else if (token == Punctuation.LeftParenthesis)
					stack.Push(token);

				else if (token == Punctuation.RightParenthesis) {
					// If its ')' pop elements from stack to output list
					// until find the '('
					Token element;

					while ((element = stack.Pop()) != Punctuation.LeftParenthesis)
						output.Add(element);
				}
				else {
					// While priority of elements at peek of stack >= (>) token's priority
					// put these elements to output list
					while (stack.Count > 0
							 && token <= stack.Peek())
						output.Add(stack.Pop());

					stack.Push(token);
				}
			}

			// Pop all elements from stack to output string            
			while (stack.Count > 0) {
				// There should be only operators
				if (stack.Peek() is Operator)
					output.Add(stack.Pop());

				else throw new FormatException("Format exception, there is function without parenthesis");
			}
			Logger.LogIfDebugging("\nTokenized Output Stack (GeneratedRPN method:");

			foreach (Token t in output) {
				Logger.LogIfDebugging(t.ToString());
			}
			Logger.LogIfDebugging("End of Tokenized Output Stack\n");

			return output;
		}

		List<Token> Tokenize(string Expression) {
			int pos = 0;
			List<Token> infix = [];

			while (pos < Expression.Length) {
				StringBuilder word = new();
				word.Append(Expression[pos]);

				if (word[0].Is('(', ')', ' ')) ++pos;
				switch (word[0]) {

					//Check for Punctuation

					case '(':
						if (infix.Count > 0) {
							if (infix.Last() is Unit || infix.Last() is Number) infix.Add(Multiply);
						}
						infix.Add(Punctuation.LeftParenthesis);
						break;

					case ' ':
						if (pos < Expression.Length && Expression[pos] == ')') break;  // skip over space if next character is a right parenthesis ')'
						if (infix.Last() is Unit || infix.Last() is Number || infix.Last().Symbol == ")") infix.Add(Multiply);
						break;

					case ')':
						infix.Add(Punctuation.RightParenthesis);
						if (pos < Expression.Length && Expression[pos] != ' ') {             // add a multiply to force a multiply if next character
							if (char.IsLetter(Expression[pos]) || Expression[pos] == '°') {   // is not a space or an arithmetic operator
								infix.Add(Multiply);
							}
						}
						break;

					//Not Punctuation

					default:

						//Does NOT start with a letter or digit - must be operator

						if (!(char.IsLetterOrDigit(word[0]) || word[0] == '°')) {
							if (pos + 1 >= Expression.Length || !char.IsLetterOrDigit(Expression[pos + 1])
																  && !Expression[pos + 1].Is(')', '(', ' ')) {
								while (++pos < Expression.Length - 1 && !char.IsLetterOrDigit(Expression[pos + 1])
																			&& !Expression[pos + 1].Is(')', '(', ' ')) {
									word.Append(Expression[pos]);
								}
								do {
									if (Operators.Contains(word.ToString())) {
										infix.Add(Operators[word.ToString()]);
										break;
									}
								} while (true);
							}
							else {

								//Check if it is a Unary or Binary Operator

								bool isUnary = pos == 0 || Expression[pos - 1] == '(' || infix.Last() is Operator;
								pos++;

								string name = word.ToString();
								switch (name) {
									case "+":
										infix.Add(isUnary ? (Operator)Identity : Plus);
										break;
									case "-":
										infix.Add(isUnary ? (Operator)Negation : Minus);
										break;
									default:
										if (Operators.Contains(name)) {
											infix.Add(Operators[name]);
										}
										else throw new TokenNotDefinedException(name);
										break;
								}
							}
						}

						//Starts with a letter

						else if (char.IsLetter(word[0]) || word[0] == '°') {
							StringBuilder partialWord = new();
							char ch = ' ';
							while (++pos < Expression.Length) {
								ch = Expression[pos];
								if (char.IsLetter(ch)) {
									word.Append(partialWord).Append(ch);
									partialWord.Clear();
									continue;
								}
								else if (ch.Is(Delimiters)) break;

								// Could be a unit raised to a power, save character as a potential word

								else {
									partialWord.Append(ch);
								}
							}

							Logger.LogIfDebugging(word.ToString());
							if (!AddUnit(word.ToString(), ref infix)) throw new UnitNotDefinedException(word.ToString());
							if (double.TryParse(partialWord.ToString(), out double exp)) {
								infix.Add(Power);
								infix.Add(new Number(exp));
							}
							else if (partialWord.ToString().Length > 0) {
								Logger.LogIfDebugging($"Throwing invalid number exception, string is {partialWord}");
								throw new InvalidNumberException(partialWord.ToString());
							}

							if (ch == ' ' && pos++ < Expression.Length) {
								if (pos < Expression.Length && !Operators.Contains(Expression[pos].ToString())) infix.Add(Multiply);
							}
						}
						else if (char.IsDigit(word[0]) || word[0] == DecimalSeparator) {
							infix.Add(new Number(ParseDouble(Expression, ref pos)));
						}
						else throw new TokenNotDefinedException(word.ToString());
						break;
				}
			}
			Logger.LogIfDebugging("\nTokenized Stack:");
			foreach (Token t in infix) {
				Logger.LogIfDebugging(t.ToString());
			}
			Logger.LogIfDebugging("End of Tokenized Stack\n");
			return infix;
		}

		double ParseDouble(string Expression, ref int pos) {


			int initialPos = pos;
			int length = Expression.Length;

			if (char.IsDigit(Expression[pos])) {
				while (++pos < length && char.IsDigit(Expression[pos])) { }
			}

			//decimal point check

			if (pos < length && Expression[pos] == DecimalSeparator) {
				while (++pos < length && char.IsDigit(Expression[pos])) { }
			}

			//scientific notation check

			if (pos + 1 < length && char.ToLower(Expression[pos]) == 'e') {   //letter e cannot be last character in Expression
				if (char.IsDigit(Expression[pos + 1]))
					while (++pos < length && char.IsDigit(Expression[pos])) { }
				else {
					foreach (string test in SciNumberFormats) {
						if (Expression.IndexOf(test, ++pos) == pos) {
							pos += test.Length - 1;
							while (++pos < length && char.IsDigit(Expression[pos])) { }
							break;
						}
					}
				}
			}
			//return double.Parse(Expression.Substring(initialPos, pos - initialPos).Replace(" ", ""));
			return double.Parse(Expression[initialPos..pos].Replace(" ", ""));
		}

		bool AddUnit(string Name, ref List<Token> infix) {
			//Unit unit = new();
			bool foundUnit = false;

			if (Units.Contains(Name)) {
				//unit = Units[Name].Copy();
				infix.Add(Units[Name].Copy());
				foundUnit = true;
			}
			else {
				foreach (string prefix in Unit.Prefixes.Keys) {
					if (Name.StartsWith(prefix)) {
						string testName = Name[prefix.Length..];
						if (Units.Contains(testName)) {
							Unit unit = Units[testName].Copy();
							unit.Multiplier = Unit.Prefixes[prefix];
							infix.Add(unit);
							foundUnit = true;
							break;
						}
					}
				}
			}

			return foundUnit;
		}


		static bool CheckIfPressure(string Expression, out string StrippedExpression) {

			StrippedExpression = "";
			Logger.LogIfDebugging(Expression);
			if (Expression == "mmHg") return false;
			foreach (string suffix in Unit.Suffixes) {
				//Logger.LogIfDebugging(suffix + Expression.EndsWith(suffix).ToString());
				if (Expression.EndsWith(suffix)) {
					//StrippedExpression = Expression.Substring(0, Expression.Length - suffix.Length);
					StrippedExpression = Expression[..^suffix.Length];
					return true;
				}
			}
			return false;
		}

		static Unit PowerFunc(Unit x, Unit y) {

			foreach (double d in y.D) {
				if (d != 0.0) throw new InvalidPowerExceptipn(x, y);      // y must not have any base units. Must be a unitless number
			}

			double[] dim = new double[x.D.Length];
			for (int i = 0; i < x.D.Length; i++) {
				dim[i] = x.D[i] * y.Factor;
			}
			//return new Unit(Math.Pow(x.Multiplier * x.Value, y.Value), dim);
			//return new Unit(x.Multiplier * Math.Pow(x.Factor, y.Factor), dim);
			return new Unit(Math.Pow(x.Multiplier * x.Factor, y.Factor), dim);
		}

		internal static void LoadTempUnits(string ResourceFile) {

			Assembly assem = Assembly.GetExecutingAssembly();
			using Stream stream = assem.GetManifestResourceStream("UCon.Resources." + ResourceFile);
			try {
				string line;
				using StreamReader rdr = new(stream);
				rdr.ReadLine();
				while ((line = rdr.ReadLine()) != null) {
					string TempUnit = line[..10].Trim();
					//string a = line.Substring(13, 19).Trim();
					string a = line[13..19].Trim();
					string b = line[37..].Trim();
					if (double.TryParse(a, out double A) && double.TryParse(b, out double B)) {
						TemperatureUnits.Add(TempUnit, new LinearCoefficients(A, B));
					}
				}
			}
			catch {
				Logger.LogIfDebugging("Error occured in LoadTempUnits while reading: " + ResourceFile);
			}

		}

		public static void AddValidLetter(char Char) { }

		public static bool IsTemperatureUnit(string Unit) {

			return TemperatureUnits.TryGetValue(Unit, out _);
		}

	} //class

}//namespace
