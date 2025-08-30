using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace UCon {
	public class UnitConverter {

		public static TokenList<Unit> SharedUnits { get; } = [];
		public static TokenList<Operator> Operators { get; } = [];

		protected static UnaryOperator Identity;
		protected static UnaryOperator Negation;

		protected static BinaryOperator Plus;
		protected static BinaryOperator Minus;
		protected static BinaryOperator Multiply;
		protected static BinaryOperator Power;


		static readonly string[] _sciNumberFormats = [" + ", " +", "+ ", "+", " - ", " -", "- ", "-", " "];

		public TokenList<Unit> Units { get; } = SharedUnits.Branch();

		static UnitConverter _sharedUnitConverter;

		public static object Calculate(string LeftSide, string RightSide = "", double? Value = null) {

			RightSide ??= "";
			Value ??= 1.0;
			//RightSide ??= "";
			//Value ??= 1.0;

			_sharedUnitConverter ??= new UnitConverter();
			Value ??= 1.0;
			Debug.WriteLine($"Left side: {LeftSide}, Right side {RightSide}, Value: {Value}");
			return _sharedUnitConverter.Convert(LeftSide, RightSide, Value);
		}

		public static Unit GetBaseUnit(string UnitExpression) {
			_sharedUnitConverter ??= new UnitConverter();
			if (UnitExpression == null) {
				return null;
			}
			else {
				return _sharedUnitConverter.BaseUnit(UnitExpression.Trim());
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

		private static readonly Dictionary<string, Line> _temperatureUnits = [];

		private Unit _unit = new(0.0, [0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0]);

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

		public double Convert(string LeftSide, string RightSide = "", double? Value = null) {

			//Unit leftSide = new Unit();
			//Unit rightSide = new Unit();
			string LeftSidetrim = LeftSide.Trim();
			string RightSidetrim = RightSide.Trim();

			bool leftSideGauge = false;
			bool rightSideGauge = false;
			bool firstPassSuccess = true;

			string strippedLeftSide = "";
			string strippedRightSide = "";

			var assembly = typeof(object).Assembly;
			var version = assembly.GetName().Version;
			Debug.Print($"====> C# Compiler Version: {version}");

			StringBuilder message = new();
			if (double.TryParse(LeftSidetrim, out double _)) message.Append("To Parameter (" + LeftSidetrim + ") ");
			if (RightSidetrim.Length > 0 && double.TryParse(RightSidetrim, out double _)) {
				if (message.Length > 0) message.Append("and From Parameter (" + RightSidetrim + ") are ");
				else message.Append("From Parameter (" + RightSidetrim + ") ");
			}
			if (message.Length > 0) {
				string err = message.ToString().EndsWith("are ") == true ? message.Append("invalid.").ToString() : message.Append("is invalid.").ToString();
				throw new InvalidParameterException(err);
			}

			if (Value != null) {
				leftSideGauge = CheckIfPressure(LeftSidetrim, out strippedLeftSide);
				rightSideGauge = RightSidetrim.Length != 0 && CheckIfPressure(RightSidetrim, out strippedRightSide);
			}

			if (Value != null && _temperatureUnits.ContainsKey(LeftSidetrim) && _temperatureUnits.ContainsKey(RightSidetrim)) {
				double T1 = _temperatureUnits[LeftSidetrim].A * (double)Value + _temperatureUnits[LeftSidetrim].B;
				return (T1 - _temperatureUnits[RightSidetrim].B) / _temperatureUnits[RightSidetrim].A;
			}

			//Get conversion factor to SI Units for the Left Side

			try {
				RPNExpression = GenerateRPN(Tokenize(FormatExpression(LeftSidetrim)));
			}
			catch (UnitNotDefinedException e) {

				//Could not convert unit, see if it is a gauge pressure unit

				firstPassSuccess = false;
				try {
					if (leftSideGauge)
						RPNExpression = GenerateRPN(Tokenize(FormatExpression(strippedLeftSide)));
					else
						throw new InvalidParameterException($"Unable to process {strippedLeftSide}");
				}
				catch (Exception) {
					throw e;
				}
			}
			Unit leftSide = EvaluateRPN(RPNExpression);

			//Left Side is gauge pressure - convert to absolute pressure

			if (!firstPassSuccess && leftSideGauge) {
				if (leftSide.M == 1 && leftSide.L == -1 && leftSide.T == -2 &&
					 leftSide.I == 0 && leftSide.J == 0 && leftSide.N == 0 && leftSide.Θ == 0) {
					leftSide.Value = leftSide.Value * (double)Value + GaugePressure;
					Value = 1.0;
					if (leftSide.Value < 0)
						throw new InvalidGaugePressureException();
				}
				else throw new InvalidPressureUnitException(strippedLeftSide);
			}

			firstPassSuccess = false;
			if (RightSidetrim.Length > 0) {
				try {
					RPNExpression = GenerateRPN(Tokenize(FormatExpression(RightSidetrim)));
					rightSideGauge = false;
				}

				//Could not convert unit, see if it is a gauge pressure unit

				catch (UnitNotDefinedException e) {
					try {
						if (rightSideGauge)
							RPNExpression = GenerateRPN(Tokenize(FormatExpression(strippedRightSide)));
						else
							throw new InvalidParameterException($"Unable to process {strippedRightSide}");
					}
					catch (Exception) {
						throw e;
					}
				}
				Unit rightSide = EvaluateRPN(RPNExpression);


				double pressureToGauge = 0;
				if (!firstPassSuccess && rightSideGauge) {
					pressureToGauge = GaugePressure / rightSide.Value;
				}

				_unit = leftSide / rightSide;
				foreach (double d in _unit.D) {
					if (Math.Abs(d / 1e-6) > 1.0) throw new BaseDimensionsDontMatchException(leftSide, rightSide);
				}
				return (Value == null) ? _unit.Value : _unit.Value * (double)Value - pressureToGauge;
			}
			return (Value == null) ? leftSide.Value : leftSide.Value * (double)Value;
		}

		static Unit EvaluateRPN(IEnumerable<Token> RPNExpression) {
			var stack = new Stack<Unit>(); // Contains operands

			// Analyze entire expression
			foreach (var token in RPNExpression) {
				// if it's operand then just push it to stack
				if (token is Unit)
					stack.Push(token as Unit);
				else if (token is Number) {
					stack.Push(token as Number);
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
			if (stack.Count > 1)
				throw new ArgumentException("Excess operand");

			//If there is only one element in the stack, must multiply by 1 to incorporate scale factor;

			if (RPNExpression.Count<Token>() == 1) return Multiply.Invoke(new Unit[2] { new Number(1.0), stack.Pop() });
			return stack.Pop();
		}

		string FormatExpression(string Expression) {

			string str = Expression.Trim();
			StringBuilder firstEdit = new(str);
			firstEdit.Replace('[', '(');
			firstEdit.Replace('{', '(');
			firstEdit.Replace(']', ')');
			firstEdit.Replace('}', ')');
			string secondString = firstEdit.ToString();
			Debug.Print($"In FormatExpression, after first edit, expression: {secondString}");

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
			Debug.Print($"In FormatExpression, after second edit, expression: {secondEdit}");
			return secondEdit.ToString();
		}

		static List<Token> GenerateRPN(IEnumerable<Token> Input) {
			var output = new List<Token>();
			var stack = new Stack<Token>();

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
			Debug.WriteLine("\nTokenized Output Stack:");

			foreach (Token t in output) {
				Debug.WriteLine(t.ToString());
			}
			Debug.WriteLine("End of Tokenized Output Stack\n");

			return output;
		}

		IEnumerable<Token> Tokenize(string Expression) {
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
						if (pos < Expression.Length && Expression[pos] == ')') break;  // skip over space if next character is a )
						if (infix.Last() is Unit || infix.Last() is Number || infix.Last().Symbol == ")") infix.Add(Multiply);
						break;

					case ')':
						infix.Add(Punctuation.RightParenthesis);
						break;

					//Not Punctuation

					default:

						//Does NOT start with a letter or digit - must be operator

						if (!(char.IsLetterOrDigit(word[0]) || word[0] == '°')) {
							if (pos + 1 >= Expression.Length || !char.IsLetterOrDigit(Expression[pos + 1])
																  && !Expression[pos + 1].Is(')', '(', ' ')) {
								while (++pos < Expression.Length && !char.IsLetterOrDigit(Expression[pos + 1])
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
									word.Append(partialWord.ToString()).Append(ch);
									partialWord.Clear();
									continue;
								}
								else if (ch.Is(Delimiters)) break;

								// Could be a unit raised to a power, save character as a potential word

								else {
									partialWord.Append(ch);
								}
							}

							Debug.WriteLine(word);
							if (!AddUnit(word.ToString(), ref infix)) throw new UnitNotDefinedException(word.ToString());
							if (double.TryParse(partialWord.ToString(), out double exp)) {
								infix.Add(Power);
								infix.Add(new Number(exp));
							}
							else if (partialWord.ToString().Length > 0) {
								Debug.WriteLine($"Throwing invalid number exception, string is {partialWord}");
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
			Debug.WriteLine("\nTokenized Stack:");
			foreach (Token t in infix) {
				Debug.WriteLine(t.ToString());
			}
			Debug.WriteLine("End of Tokenized Stack\n");
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
					foreach (string test in _sciNumberFormats) {
						if (Expression.IndexOf(test, ++pos) == pos) {
							pos += test.Length - 1;
							while (++pos < length && char.IsDigit(Expression[pos])) { }
							break;
						}
					}
				}
			}
			return double.Parse(Expression.Substring(initialPos, pos - initialPos).Replace(" ", ""));
		}

		bool AddUnit(string Name, ref List<Token> infix) {
			//_ = new Unit();
			bool foundUnit = false;
			Unit unit;
			if (Units.Contains(Name)) {
				unit = Units[Name].Copy();
				infix.Add(unit);
				foundUnit = true;
			}
			else {
				foreach (string prefix in Unit.Prefixes.Keys) {
					if (Name.StartsWith(prefix)) {
						string testName = Name.Substring(prefix.Length);
						if (Units.Contains(testName)) {
							unit = Units[testName].Copy();
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


		bool CheckIfPressure(string Expression, out string StrippedExpression) {

			StrippedExpression = "";
			Debug.WriteLine(Expression);
			if (Expression == "mmHg") return false;
			foreach (string suffix in Unit.Suffixes) {
				//Debug.WriteLine(suffix + Expression.EndsWith(suffix).ToString());
				if (Expression.EndsWith(suffix)) {
					StrippedExpression = Expression.Substring(0, Expression.Length - suffix.Length);
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
				dim[i] = x.D[i] * y.Value;
			}
			return new Unit(Math.Pow(x.Multiplier * x.Value, y.Value), dim);
		}

		internal static void LoadTempUnits(string ResourceFile) {

			Assembly assem = Assembly.GetExecutingAssembly();
			using Stream stream = assem.GetManifestResourceStream("UCon.Resources." + ResourceFile);
			try {
				string line;
				using StreamReader rdr = new(stream);
				rdr.ReadLine();
				while ((line = rdr.ReadLine()) != null) {
					string TempUnit = line.Substring(0, 10).Trim();
					string a = line.Substring(13, 19).Trim();
					string b = line.Substring(37).Trim();
					if (double.TryParse(a, out double A) && double.TryParse(b, out double B)) {
						_temperatureUnits.Add(TempUnit, new Line(A, B));
					}
				}
			}
			catch {
				Debug.WriteLine("Error occured in LoadTempUnits while reading: " + ResourceFile);
			}

		}

	} //class

}//namespace
