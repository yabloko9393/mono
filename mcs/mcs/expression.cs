//
// expression.cs: Expression representation for the IL tree.
//
// Author:
//   Miguel de Icaza (miguel@ximian.com)
//
// (C) 2001 Ximian, Inc.
//
//

namespace CIR {
	using System;
	using System.Collections;
	using System.Diagnostics;
	using System.Reflection;
	using System.Reflection.Emit;
	using System.Text;

	// <summary>
	//   Unary expressions.  
	// </summary>
	//
	// <remarks>
	//   Unary implements unary expressions.   It derives from
	//   ExpressionStatement becuase the pre/post increment/decrement
	//   operators can be used in a statement context.
	// </remarks>
	public class Unary : ExpressionStatement {
		public enum Operator {
			UnaryPlus, UnaryNegation, LogicalNot, OnesComplement,
			Indirection, AddressOf, PreIncrement,
			PreDecrement, PostIncrement, PostDecrement 
		}

		Operator   oper;
		Expression expr;
		ArrayList  Arguments;
		MethodBase method;
		Location   loc;
		
		public Unary (Operator op, Expression expr, Location loc)
		{
			this.oper = op;
			this.expr = expr;
			this.loc = loc;
		}

		public Expression Expr {
			get {
				return expr;
			}

			set {
				expr = value;
			}
		}

		public Operator Oper {
			get {
				return oper;
			}

			set {
				oper = value;
			}
		}

		// <summary>
		//   Returns a stringified representation of the Operator
		// </summary>
		string OperName ()
		{
			switch (oper){
			case Operator.UnaryPlus:
				return "+";
			case Operator.UnaryNegation:
				return "-";
			case Operator.LogicalNot:
				return "!";
			case Operator.OnesComplement:
				return "~";
			case Operator.AddressOf:
				return "&";
			case Operator.Indirection:
				return "*";
			case Operator.PreIncrement : case Operator.PostIncrement :
				return "++";
			case Operator.PreDecrement : case Operator.PostDecrement :
				return "--";
			}

			return oper.ToString ();
		}

		Expression ForceConversion (EmitContext ec, Expression expr, Type target_type)
		{
			if (expr.Type == target_type)
				return expr;

			return ConvertImplicit (ec, expr, target_type, new Location (-1));
		}

		void error23 (Type t)
		{
			Report.Error (
				23, loc, "Operator " + OperName () +
				" cannot be applied to operand of type `" +
				TypeManager.CSharpName (t) + "'");
		}

		// <summary>
		//   Returns whether an object of type `t' can be incremented
		//   or decremented with add/sub (ie, basically whether we can
		//   use pre-post incr-decr operations on it, but it is not a
		//   System.Decimal, which we test elsewhere)
		// </summary>
		static bool IsIncrementableNumber (Type t)
		{
			return (t == TypeManager.sbyte_type) ||
				(t == TypeManager.byte_type) ||
				(t == TypeManager.short_type) ||
				(t == TypeManager.ushort_type) ||
				(t == TypeManager.int32_type) ||
				(t == TypeManager.uint32_type) ||
				(t == TypeManager.int64_type) ||
				(t == TypeManager.uint64_type) ||
				(t == TypeManager.char_type) ||
				(t.IsSubclassOf (TypeManager.enum_type)) ||
				(t == TypeManager.float_type) ||
				(t == TypeManager.double_type);
		}

		static Expression TryReduceNegative (Expression expr)
		{
			Expression e = null;
			
			if (expr is IntLiteral)
				e = new IntLiteral (-((IntLiteral) expr).Value);
			else if (expr is UIntLiteral)
				e = new LongLiteral (-((UIntLiteral) expr).Value);
			else if (expr is LongLiteral)
				e = new LongLiteral (-((LongLiteral) expr).Value);
			else if (expr is FloatLiteral)
				e = new FloatLiteral (-((FloatLiteral) expr).Value);
			else if (expr is DoubleLiteral)
				e = new DoubleLiteral (-((DoubleLiteral) expr).Value);
			else if (expr is DecimalLiteral)
				e = new DecimalLiteral (-((DecimalLiteral) expr).Value);

			return e;
		}
		
		Expression ResolveOperator (EmitContext ec)
		{
			Type expr_type = expr.Type;

			//
			// Step 1: Perform Operator Overload location
			//
			Expression mg;
			string op_name;
			
			if (oper == Operator.PostIncrement || oper == Operator.PreIncrement)
				op_name = "op_Increment";
			else if (oper == Operator.PostDecrement || oper == Operator.PreDecrement)
				op_name = "op_Decrement";
			else
				op_name = "op_" + oper;

			mg = MemberLookup (ec, expr_type, op_name, false, loc);
			
			if (mg == null && expr_type.BaseType != null)
				mg = MemberLookup (ec, expr_type.BaseType, op_name, false, loc);
			
			if (mg != null) {
				Arguments = new ArrayList ();
				Arguments.Add (new Argument (expr, Argument.AType.Expression));
				
				method = Invocation.OverloadResolve (ec, (MethodGroupExpr) mg,
								     Arguments, loc);
				if (method != null) {
					MethodInfo mi = (MethodInfo) method;
					type = mi.ReturnType;
					return this;
				} else {
					error23 (expr_type);
					return null;
				}
					
			}

			//
			// Step 2: Default operations on CLI native types.
			//

			// Only perform numeric promotions on:
			// +, -, ++, --

			if (expr_type == null)
				return null;
			
			if (oper == Operator.LogicalNot){
				if (expr_type != TypeManager.bool_type) {
					error23 (expr.Type);
					return null;
				}
				
				type = TypeManager.bool_type;
				return this;
			}

			if (oper == Operator.OnesComplement) {
				if (!((expr_type == TypeManager.int32_type) ||
				      (expr_type == TypeManager.uint32_type) ||
				      (expr_type == TypeManager.int64_type) ||
				      (expr_type == TypeManager.uint64_type) ||
				      (expr_type.IsSubclassOf (TypeManager.enum_type)))){
					error23 (expr.Type);
					return null;
				}
				type = expr_type;
				return this;
			}

			if (oper == Operator.UnaryPlus) {
				//
				// A plus in front of something is just a no-op, so return the child.
				//
				return expr;
			}

			//
			// Deals with -literals
			// int     operator- (int x)
			// long    operator- (long x)
			// float   operator- (float f)
			// double  operator- (double d)
			// decimal operator- (decimal d)
			//
			if (oper == Operator.UnaryNegation){
				//
				// Fold a "- Constant" into a negative constant
				//
			
				Expression e = null;

				//
				// Is this a constant? 
				//
				e = TryReduceNegative (expr);
				
				if (e != null){
					e = e.Resolve (ec);
					return e;
				}

				//
				// Not a constant we can optimize, perform numeric 
				// promotions to int, long, double.
				//
				//
				// The following is inneficient, because we call
				// ConvertImplicit too many times.
				//
				// It is also not clear if we should convert to Float
				// or Double initially.
				//
				if (expr_type == TypeManager.uint32_type){
					//
					// FIXME: handle exception to this rule that
					// permits the int value -2147483648 (-2^31) to
					// bt written as a decimal interger literal
					//
					type = TypeManager.int64_type;
					expr = ConvertImplicit (ec, expr, type, loc);
					return this;
				}

				if (expr_type == TypeManager.uint64_type){
					//
					// FIXME: Handle exception of `long value'
					// -92233720368547758087 (-2^63) to be written as
					// decimal integer literal.
					//
					error23 (expr_type);
					return null;
				}

				e = ConvertImplicit (ec, expr, TypeManager.int32_type, loc);
				if (e != null){
					expr = e;
					type = e.Type;
					return this;
				} 

				e = ConvertImplicit (ec, expr, TypeManager.int64_type, loc);
				if (e != null){
					expr = e;
					type = e.Type;
					return this;
				}

				e = ConvertImplicit (ec, expr, TypeManager.double_type, loc);
				if (e != null){
					expr = e;
					type = e.Type;
					return this;
				}

				error23 (expr_type);
				return null;
			}

			//
			// The operand of the prefix/postfix increment decrement operators
			// should be an expression that is classified as a variable,
			// a property access or an indexer access
			//
			if (oper == Operator.PreDecrement || oper == Operator.PreIncrement ||
			    oper == Operator.PostDecrement || oper == Operator.PostIncrement){
				if (expr.ExprClass == ExprClass.Variable){
					if (IsIncrementableNumber (expr_type) ||
					    expr_type == TypeManager.decimal_type){
						type = expr_type;
						return this;
					}
				} else if (expr.ExprClass == ExprClass.IndexerAccess){
					//
					// FIXME: Verify that we have both get and set methods
					//
					throw new Exception ("Implement me");
				} else if (expr.ExprClass == ExprClass.PropertyAccess){
					PropertyExpr pe = (PropertyExpr) expr;
					
					if (pe.VerifyAssignable ())
						return this;
					return null;
				} else {
					report118 (loc, expr, "variable, indexer or property access");
				}
			}

			if (oper == Operator.AddressOf){
				if (expr.ExprClass != ExprClass.Variable){
					Error (211, "Cannot take the address of non-variables");
					return null;
				}
				type = Type.GetType (expr.Type.ToString () + "*");
			}
			
			Error (187, "No such operator '" + OperName () + "' defined for type '" +
			       TypeManager.CSharpName (expr_type) + "'");
			return null;

		}

		public override Expression DoResolve (EmitContext ec)
		{
			expr = expr.Resolve (ec);
			
			if (expr == null)
				return null;

			eclass = ExprClass.Value;
			return ResolveOperator (ec);
		}

		public override void Emit (EmitContext ec)
		{
			ILGenerator ig = ec.ig;
			Type expr_type = expr.Type;
			ExprClass eclass;
			
			if (method != null) {

				// Note that operators are static anyway
				
				if (Arguments != null) 
					Invocation.EmitArguments (ec, Arguments);

				//
				// Post increment/decrement operations need a copy at this
				// point.
				//
				if (oper == Operator.PostDecrement || oper == Operator.PostIncrement)
					ig.Emit (OpCodes.Dup);
				

				ig.Emit (OpCodes.Call, (MethodInfo) method);

				//
				// Pre Increment and Decrement operators
				//
				if (oper == Operator.PreIncrement || oper == Operator.PreDecrement){
					ig.Emit (OpCodes.Dup);
				}
				
				//
				// Increment and Decrement should store the result
				//
				if (oper == Operator.PreDecrement || oper == Operator.PreIncrement ||
				    oper == Operator.PostDecrement || oper == Operator.PostIncrement){
					((IStackStore) expr).Store (ec);
				}
				return;
			}
			
			switch (oper) {
			case Operator.UnaryPlus:
				throw new Exception ("This should be caught by Resolve");
				
			case Operator.UnaryNegation:
				expr.Emit (ec);
				ig.Emit (OpCodes.Neg);
				break;
				
			case Operator.LogicalNot:
				expr.Emit (ec);
				ig.Emit (OpCodes.Ldc_I4_0);
				ig.Emit (OpCodes.Ceq);
				break;
				
			case Operator.OnesComplement:
				expr.Emit (ec);
				ig.Emit (OpCodes.Not);
				break;
				
			case Operator.AddressOf:
				((IMemoryLocation)expr).AddressOf (ec);
				break;
				
			case Operator.Indirection:
				throw new Exception ("Not implemented yet");
				
			case Operator.PreIncrement:
			case Operator.PreDecrement:
				if (expr.ExprClass == ExprClass.Variable){
					//
					// Resolve already verified that it is an "incrementable"
					// 
					expr.Emit (ec);
					ig.Emit (OpCodes.Ldc_I4_1);
					
					if (oper == Operator.PreDecrement)
						ig.Emit (OpCodes.Sub);
					else
						ig.Emit (OpCodes.Add);
					ig.Emit (OpCodes.Dup);
					((IStackStore) expr).Store (ec);
				} else {
					throw new Exception ("Handle Indexers and Properties here");
				}
				break;
				
			case Operator.PostIncrement:
			case Operator.PostDecrement:
				eclass = expr.ExprClass;
				if (eclass == ExprClass.Variable){
					//
					// Resolve already verified that it is an "incrementable"
					// 
					expr.Emit (ec);
					ig.Emit (OpCodes.Dup);
					ig.Emit (OpCodes.Ldc_I4_1);
					
					if (oper == Operator.PostDecrement)
						ig.Emit (OpCodes.Sub);
					else
						ig.Emit (OpCodes.Add);
					((IStackStore) expr).Store (ec);
				} else if (eclass == ExprClass.PropertyAccess){
					throw new Exception ("Handle Properties here");
				} else if (eclass == ExprClass.IndexerAccess) {
					throw new Exception ("Handle Indexers here");
				} else {
					Console.WriteLine ("Unknown exprclass: " + eclass);
				}
				break;
				
			default:
				throw new Exception ("This should not happen: Operator = "
						     + oper.ToString ());
			}
		}

		// <summary>
		//   This will emit the child expression for `ec' avoiding the logical
		//   not.  The parent will take care of changing brfalse/brtrue
		// </summary>
		public void EmitLogicalNot (EmitContext ec)
		{
			if (oper != Operator.LogicalNot)
				throw new Exception ("EmitLogicalNot can only be called with !expr");

			expr.Emit (ec);
		}
		
		public override void EmitStatement (EmitContext ec)
		{
			//
			// FIXME: we should rewrite this code to generate
			// better code for ++ and -- as we know we wont need
			// the values on the stack
			//
			Emit (ec);
			ec.ig.Emit (OpCodes.Pop);
		}

		public override Expression Reduce (EmitContext ec)
		{
			Expression e;
			
			//
			// We can not reduce expressions that invoke operator overloaded functions.
			//
			if (method != null)
				return this;

			//
			// First, reduce our child.  Note that although we handle 
			//
			expr = expr.Reduce (ec);
			if (!(expr is Literal))
				return expr;
			
			switch (oper){
			case Operator.UnaryPlus:
				return expr;
				
			case Operator.UnaryNegation:
				e = TryReduceNegative (expr);
				if (e == null)
					break;
				return e;
				
			case Operator.LogicalNot:
				BoolLiteral b = (BoolLiteral) expr;

				return new BoolLiteral (!(b.Value));
				
			case Operator.OnesComplement:
				Type et = expr.Type;
				
				if (et == TypeManager.int32_type)
					return new IntLiteral (~ ((IntLiteral) expr).Value);
				if (et == TypeManager.uint32_type)
					return new UIntLiteral (~ ((UIntLiteral) expr).Value);
				if (et == TypeManager.int64_type)
					return new LongLiteral (~ ((LongLiteral) expr).Value);
				if (et == TypeManager.uint64_type)
					return new ULongLiteral (~ ((ULongLiteral) expr).Value);
				break;
			}
			return this;
		}
	}
	
	public class Probe : Expression {
		public readonly string ProbeType;
		public readonly Operator Oper;
		Expression expr;
		Type probe_type;
		
		public enum Operator {
			Is, As
		}
		
		public Probe (Operator oper, Expression expr, string probe_type)
		{
			Oper = oper;
			ProbeType = probe_type;
			this.expr = expr;
		}

		public Expression Expr {
			get {
				return expr;
			}
		}
		
		public override Expression DoResolve (EmitContext ec)
		{
			probe_type = ec.TypeContainer.LookupType (ProbeType, false);

			if (probe_type == null)
				return null;

			expr = expr.Resolve (ec);
			
			type = TypeManager.bool_type;
			eclass = ExprClass.Value;

			return this;
		}

		public override void Emit (EmitContext ec)
		{
			ILGenerator ig = ec.ig;
			
			expr.Emit (ec);
			
			if (Oper == Operator.Is){
				ig.Emit (OpCodes.Isinst, probe_type);
				ig.Emit (OpCodes.Ldnull);
				ig.Emit (OpCodes.Cgt_Un);
			} else {
				ig.Emit (OpCodes.Isinst, probe_type);
			}
		}
	}

	// <summary>
	//   This represents a typecast in the source language.
	//
	//   FIXME: Cast expressions have an unusual set of parsing
	//   rules, we need to figure those out.
	// </summary>
	public class Cast : Expression {
		string target_type;
		Expression expr;
		Location   loc;
			
		public Cast (string cast_type, Expression expr, Location loc)
		{
			this.target_type = cast_type;
			this.expr = expr;
			this.loc = loc;
		}

		public string TargetType {
			get {
				return target_type;
			}
		}

		public Expression Expr {
			get {
				return expr;
			}
			set {
				expr = value;
			}
		}
		
		public override Expression DoResolve (EmitContext ec)
		{
			expr = expr.Resolve (ec);
			if (expr == null)
				return null;
			
			type = ec.TypeContainer.LookupType (target_type, false);
			eclass = ExprClass.Value;
			
			if (type == null)
				return null;

			expr = ConvertExplicit (ec, expr, type, loc);

			return expr;
		}

		public override void Emit (EmitContext ec)
		{
			//
			// This one will never happen
			//
			throw new Exception ("Should not happen");
		}
	}

	public class Binary : Expression {
		public enum Operator {
			Multiply, Division, Modulus,
			Addition, Subtraction,
			LeftShift, RightShift,
			LessThan, GreaterThan, LessThanOrEqual, GreaterThanOrEqual, 
			Equality, Inequality,
			BitwiseAnd,
			ExclusiveOr,
			BitwiseOr,
			LogicalAnd,
			LogicalOr
		}

		Operator oper;
		Expression left, right;
		MethodBase method;
		ArrayList  Arguments;
		Location   loc;
		

		public Binary (Operator oper, Expression left, Expression right, Location loc)
		{
			this.oper = oper;
			this.left = left;
			this.right = right;
			this.loc = loc;
		}

		public Operator Oper {
			get {
				return oper;
			}
			set {
				oper = value;
			}
		}
		
		public Expression Left {
			get {
				return left;
			}
			set {
				left = value;
			}
		}

		public Expression Right {
			get {
				return right;
			}
			set {
				right = value;
			}
		}


		// <summary>
		//   Returns a stringified representation of the Operator
		// </summary>
		string OperName ()
		{
			switch (oper){
			case Operator.Multiply:
				return "*";
			case Operator.Division:
				return "/";
			case Operator.Modulus:
				return "%";
			case Operator.Addition:
				return "+";
			case Operator.Subtraction:
				return "-";
			case Operator.LeftShift:
				return "<<";
			case Operator.RightShift:
				return ">>";
			case Operator.LessThan:
				return "<";
			case Operator.GreaterThan:
				return ">";
			case Operator.LessThanOrEqual:
				return "<=";
			case Operator.GreaterThanOrEqual:
				return ">=";
			case Operator.Equality:
				return "==";
			case Operator.Inequality:
				return "!=";
			case Operator.BitwiseAnd:
				return "&";
			case Operator.BitwiseOr:
				return "|";
			case Operator.ExclusiveOr:
				return "^";
			case Operator.LogicalOr:
				return "||";
			case Operator.LogicalAnd:
				return "&&";
			}

			return oper.ToString ();
		}

		Expression ForceConversion (EmitContext ec, Expression expr, Type target_type)
		{
			if (expr.Type == target_type)
				return expr;

			return ConvertImplicit (ec, expr, target_type, new Location (-1));
		}
		
		//
		// Note that handling the case l == Decimal || r == Decimal
		// is taken care of by the Step 1 Operator Overload resolution.
		//
		void DoNumericPromotions (EmitContext ec, Type l, Type r)
		{
			if (l == TypeManager.double_type || r == TypeManager.double_type){
				//
				// If either operand is of type double, the other operand is
				// conveted to type double.
				//
				if (r != TypeManager.double_type)
					right = ConvertImplicit (ec, right, TypeManager.double_type, loc);
				if (l != TypeManager.double_type)
					left = ConvertImplicit (ec, left, TypeManager.double_type, loc);
				
				type = TypeManager.double_type;
			} else if (l == TypeManager.float_type || r == TypeManager.float_type){
				//
				// if either operand is of type float, th eother operand is
				// converd to type float.
				//
				if (r != TypeManager.double_type)
					right = ConvertImplicit (ec, right, TypeManager.float_type, loc);
				if (l != TypeManager.double_type)
					left = ConvertImplicit (ec, left, TypeManager.float_type, loc);
				type = TypeManager.float_type;
			} else if (l == TypeManager.uint64_type || r == TypeManager.uint64_type){
				Expression e;
				Type other;
				//
				// If either operand is of type ulong, the other operand is
				// converted to type ulong.  or an error ocurrs if the other
				// operand is of type sbyte, short, int or long
				//
				
				if (l == TypeManager.uint64_type){
					if (r != TypeManager.uint64_type && right is IntLiteral){
						e = TryImplicitIntConversion (l, (IntLiteral) right);
						if (e != null)
							right = e;
					}
					other = right.Type;
				} else {
					if (left is IntLiteral){
						e = TryImplicitIntConversion (r, (IntLiteral) left);
						if (e != null)
							left = e;
					}
					other = left.Type;
				}

				if ((other == TypeManager.sbyte_type) ||
				    (other == TypeManager.short_type) ||
				    (other == TypeManager.int32_type) ||
				    (other == TypeManager.int64_type)){
					string oper = OperName ();
					
					Error (34, loc, "Operator `" + OperName ()
					       + "' is ambiguous on operands of type `"
					       + TypeManager.CSharpName (l) + "' "
					       + "and `" + TypeManager.CSharpName (r)
					       + "'");
				}
				type = TypeManager.uint64_type;
			} else if (l == TypeManager.int64_type || r == TypeManager.int64_type){
				//
				// If either operand is of type long, the other operand is converted
				// to type long.
				//
				if (l != TypeManager.int64_type)
					left = ConvertImplicit (ec, left, TypeManager.int64_type, loc);
				if (r != TypeManager.int64_type)
					right = ConvertImplicit (ec, right, TypeManager.int64_type, loc);

				type = TypeManager.int64_type;
			} else if (l == TypeManager.uint32_type || r == TypeManager.uint32_type){
				//
				// If either operand is of type uint, and the other
				// operand is of type sbyte, short or int, othe operands are
				// converted to type long.
				//
				Type other = null;
				
				if (l == TypeManager.uint32_type)
					other = r;
				else if (r == TypeManager.uint32_type)
					other = l;

				if ((other == TypeManager.sbyte_type) ||
				    (other == TypeManager.short_type) ||
				    (other == TypeManager.int32_type)){
					left = ForceConversion (ec, left, TypeManager.int64_type);
					right = ForceConversion (ec, right, TypeManager.int64_type);
					type = TypeManager.int64_type;
				} else {
					//
					// if either operand is of type uint, the other
					// operand is converd to type uint
					//
					left = ForceConversion (ec, left, TypeManager.uint32_type);
					right = ForceConversion (ec, right, TypeManager.uint32_type);
					type = TypeManager.uint32_type;
				} 
			} else if (l == TypeManager.decimal_type || r == TypeManager.decimal_type){
				if (l != TypeManager.decimal_type)
					left = ConvertImplicit (ec, left, TypeManager.decimal_type, loc);
				if (r != TypeManager.decimal_type)
					right = ConvertImplicit (ec, right, TypeManager.decimal_type, loc);

				type = TypeManager.decimal_type;
			} else {
				Expression l_tmp, r_tmp;

				l_tmp = ForceConversion (ec, left, TypeManager.int32_type);
				if (l_tmp == null) {
					error19 ();
					left = l_tmp;
					return;
				}
				
				r_tmp = ForceConversion (ec, right, TypeManager.int32_type);
				if (r_tmp == null) {
					error19 ();
					right = r_tmp;
					return;
				}
				
				type = TypeManager.int32_type;
			}
		}

		void error19 ()
		{
			Error (19, loc,
			       "Operator " + OperName () + " cannot be applied to operands of type `" +
			       TypeManager.CSharpName (left.Type) + "' and `" +
			       TypeManager.CSharpName (right.Type) + "'");
						     
		}
		
		Expression CheckShiftArguments (EmitContext ec)
		{
			Expression e;
			Type l = left.Type;
			Type r = right.Type;

			e = ForceConversion (ec, right, TypeManager.int32_type);
			if (e == null){
				error19 ();
				return null;
			}
			right = e;

			if (((e = ConvertImplicit (ec, left, TypeManager.int32_type, loc)) != null) ||
			    ((e = ConvertImplicit (ec, left, TypeManager.uint32_type, loc)) != null) ||
			    ((e = ConvertImplicit (ec, left, TypeManager.int64_type, loc)) != null) ||
			    ((e = ConvertImplicit (ec, left, TypeManager.uint64_type, loc)) != null)){
				left = e;
				type = e.Type;

				return this;
			}
			error19 ();
			return null;
		}
		
		Expression ResolveOperator (EmitContext ec)
		{
			Type l = left.Type;
			Type r = right.Type;

			//
			// Step 1: Perform Operator Overload location
			//
			Expression left_expr, right_expr;
			
			string op = "op_" + oper;

			left_expr = MemberLookup (ec, l, op, false, loc);
			if (left_expr == null && l.BaseType != null)
				left_expr = MemberLookup (ec, l.BaseType, op, false, loc);
			
			right_expr = MemberLookup (ec, r, op, false, loc);
			if (right_expr == null && r.BaseType != null)
				right_expr = MemberLookup (ec, r.BaseType, op, false, loc);
			
			MethodGroupExpr union = Invocation.MakeUnionSet (left_expr, right_expr);
			
			if (union != null) {
				Arguments = new ArrayList ();
				Arguments.Add (new Argument (left, Argument.AType.Expression));
				Arguments.Add (new Argument (right, Argument.AType.Expression));
				
				method = Invocation.OverloadResolve (ec, union, Arguments, loc);
				if (method != null) {
					MethodInfo mi = (MethodInfo) method;
					type = mi.ReturnType;
					return this;
				} else {
					error19 ();
					return null;
				}
			}	

			//
			// Step 2: Default operations on CLI native types.
			//
			
			// Only perform numeric promotions on:
			// +, -, *, /, %, &, |, ^, ==, !=, <, >, <=, >=
			//
			if (oper == Operator.Addition){
				//
				// If any of the arguments is a string, cast to string
				//
				if (l == TypeManager.string_type){
					if (r == TypeManager.string_type){
						if (left is Literal && right is Literal){
							StringLiteral ls = (StringLiteral) left;
							StringLiteral rs = (StringLiteral) right;
							
							return new StringLiteral (ls.Value + rs.Value);
						}
						
						// string + string
						method = TypeManager.string_concat_string_string;
					} else {
						// string + object
						method = TypeManager.string_concat_object_object;
						right = ConvertImplicit (ec, right,
									 TypeManager.object_type, loc);
					}
					type = TypeManager.string_type;

					Arguments = new ArrayList ();
					Arguments.Add (new Argument (left, Argument.AType.Expression));
					Arguments.Add (new Argument (right, Argument.AType.Expression));

					return this;
					
				} else if (r == TypeManager.string_type){
					// object + string
					method = TypeManager.string_concat_object_object;
					Arguments = new ArrayList ();
					Arguments.Add (new Argument (left, Argument.AType.Expression));
					Arguments.Add (new Argument (right, Argument.AType.Expression));

					left = ConvertImplicit (ec, left, TypeManager.object_type, loc);
					type = TypeManager.string_type;

					return this;
				}

				//
				// FIXME: is Delegate operator + (D x, D y) handled?
				//
			}
			
			if (oper == Operator.LeftShift || oper == Operator.RightShift)
				return CheckShiftArguments (ec);

			if (oper == Operator.LogicalOr || oper == Operator.LogicalAnd){
				if (l != TypeManager.bool_type || r != TypeManager.bool_type){
					error19 ();
					return null;
				}

				type = TypeManager.bool_type;
				return this;
			} 

			if (oper == Operator.Equality || oper == Operator.Inequality){
				if (l == TypeManager.bool_type || r == TypeManager.bool_type){
					if (r != TypeManager.bool_type || l != TypeManager.bool_type){
						error19 ();
						return null;
					}
					
					type = TypeManager.bool_type;
					return this;
				}
				//
				// fall here.
				//
			}

			//
			// We are dealing with numbers
			//

			DoNumericPromotions (ec, l, r);

			if (left == null || right == null)
				return null;

			
			if (oper == Operator.BitwiseAnd ||
			    oper == Operator.BitwiseOr ||
			    oper == Operator.ExclusiveOr){
				if (!((l == TypeManager.int32_type) ||
				      (l == TypeManager.uint32_type) ||
				      (l == TypeManager.int64_type) ||
				      (l == TypeManager.uint64_type))){
					error19 ();
					return null;
				}
				type = l;
			}

			if (oper == Operator.Equality ||
			    oper == Operator.Inequality ||
			    oper == Operator.LessThanOrEqual ||
			    oper == Operator.LessThan ||
			    oper == Operator.GreaterThanOrEqual ||
			    oper == Operator.GreaterThan){
				type = TypeManager.bool_type;
			}

			return this;
		}
		
		public override Expression DoResolve (EmitContext ec)
		{
			left = left.Resolve (ec);
			right = right.Resolve (ec);

			if (left == null || right == null)
				return null;

			if (left.Type == null)
				throw new Exception (
					"Resolve returned non null, but did not set the type! (" +
					left + ") at Line: " + loc.Row);
			if (right.Type == null)
				throw new Exception (
					"Resolve returned non null, but did not set the type! (" +
					right + ") at Line: "+ loc.Row);

			eclass = ExprClass.Value;

			return ResolveOperator (ec);
		}

		public bool IsBranchable ()
		{
			if (oper == Operator.Equality ||
			    oper == Operator.Inequality ||
			    oper == Operator.LessThan ||
			    oper == Operator.GreaterThan ||
			    oper == Operator.LessThanOrEqual ||
			    oper == Operator.GreaterThanOrEqual){
				return true;
			} else
				return false;
		}

		// <summary>
		//   This entry point is used by routines that might want
		//   to emit a brfalse/brtrue after an expression, and instead
		//   they could use a more compact notation.
		//
		//   Typically the code would generate l.emit/r.emit, followed
		//   by the comparission and then a brtrue/brfalse.  The comparissions
		//   are sometimes inneficient (there are not as complete as the branches
		//   look for the hacks in Emit using double ceqs).
		//
		//   So for those cases we provide EmitBranchable that can emit the
		//   branch with the test
		// </summary>
		public void EmitBranchable (EmitContext ec, int target)
		{
			OpCode opcode;
			bool close_target = false;
			
			left.Emit (ec);
			right.Emit (ec);
			
			switch (oper){
			case Operator.Equality:
				if (close_target)
					opcode = OpCodes.Beq_S;
				else
					opcode = OpCodes.Beq;
				break;

			case Operator.Inequality:
				if (close_target)
					opcode = OpCodes.Bne_Un_S;
				else
					opcode = OpCodes.Bne_Un;
				break;

			case Operator.LessThan:
				if (close_target)
					opcode = OpCodes.Blt_S;
				else
					opcode = OpCodes.Blt;
				break;

			case Operator.GreaterThan:
				if (close_target)
					opcode = OpCodes.Bgt_S;
				else
					opcode = OpCodes.Bgt;
				break;

			case Operator.LessThanOrEqual:
				if (close_target)
					opcode = OpCodes.Ble_S;
				else
					opcode = OpCodes.Ble;
				break;

			case Operator.GreaterThanOrEqual:
				if (close_target)
					opcode = OpCodes.Bge_S;
				else
					opcode = OpCodes.Ble;
				break;

			default:
				throw new Exception ("EmitBranchable called on non-EmitBranchable operator: "
						     + oper.ToString ());
			}

			ec.ig.Emit (opcode, target);
		}
		
		public override void Emit (EmitContext ec)
		{
			ILGenerator ig = ec.ig;
			Type l = left.Type;
			Type r = right.Type;
			OpCode opcode;

			if (method != null) {

				// Note that operators are static anyway
				
				if (Arguments != null) 
					Invocation.EmitArguments (ec, Arguments);
				
				if (method is MethodInfo)
					ig.Emit (OpCodes.Call, (MethodInfo) method);
				else
					ig.Emit (OpCodes.Call, (ConstructorInfo) method);

				return;
			}
			
			left.Emit (ec);
			right.Emit (ec);

			switch (oper){
			case Operator.Multiply:
				if (ec.CheckState){
					if (l == TypeManager.int32_type || l == TypeManager.int64_type)
						opcode = OpCodes.Mul_Ovf;
					else if (l==TypeManager.uint32_type || l==TypeManager.uint64_type)
						opcode = OpCodes.Mul_Ovf_Un;
					else
						opcode = OpCodes.Mul;
				} else
					opcode = OpCodes.Mul;

				break;

			case Operator.Division:
				if (l == TypeManager.uint32_type || l == TypeManager.uint64_type)
					opcode = OpCodes.Div_Un;
				else
					opcode = OpCodes.Div;
				break;

			case Operator.Modulus:
				if (l == TypeManager.uint32_type || l == TypeManager.uint64_type)
					opcode = OpCodes.Rem_Un;
				else
					opcode = OpCodes.Rem;
				break;

			case Operator.Addition:
				if (ec.CheckState){
					if (l == TypeManager.int32_type || l == TypeManager.int64_type)
						opcode = OpCodes.Add_Ovf;
					else if (l==TypeManager.uint32_type || l==TypeManager.uint64_type)
						opcode = OpCodes.Add_Ovf_Un;
					else
						opcode = OpCodes.Mul;
				} else
					opcode = OpCodes.Add;
				break;

			case Operator.Subtraction:
				if (ec.CheckState){
					if (l == TypeManager.int32_type || l == TypeManager.int64_type)
						opcode = OpCodes.Sub_Ovf;
					else if (l==TypeManager.uint32_type || l==TypeManager.uint64_type)
						opcode = OpCodes.Sub_Ovf_Un;
					else
						opcode = OpCodes.Sub;
				} else
					opcode = OpCodes.Sub;
				break;

			case Operator.RightShift:
				opcode = OpCodes.Shr;
				break;
				
			case Operator.LeftShift:
				opcode = OpCodes.Shl;
				break;

			case Operator.Equality:
				opcode = OpCodes.Ceq;
				break;

			case Operator.Inequality:
				ec.ig.Emit (OpCodes.Ceq);
				ec.ig.Emit (OpCodes.Ldc_I4_0);
				
				opcode = OpCodes.Ceq;
				break;

			case Operator.LessThan:
				opcode = OpCodes.Clt;
				break;

			case Operator.GreaterThan:
				opcode = OpCodes.Cgt;
				break;

			case Operator.LessThanOrEqual:
				ec.ig.Emit (OpCodes.Cgt);
				ec.ig.Emit (OpCodes.Ldc_I4_0);
				
				opcode = OpCodes.Ceq;
				break;

			case Operator.GreaterThanOrEqual:
				ec.ig.Emit (OpCodes.Clt);
				ec.ig.Emit (OpCodes.Ldc_I4_1);
				
				opcode = OpCodes.Sub;
				break;

			case Operator.LogicalOr:
			case Operator.BitwiseOr:
				opcode = OpCodes.Or;
				break;

			case Operator.LogicalAnd:
			case Operator.BitwiseAnd:
				opcode = OpCodes.And;
				break;

			case Operator.ExclusiveOr:
				opcode = OpCodes.Xor;
				break;

			default:
				throw new Exception ("This should not happen: Operator = "
						     + oper.ToString ());
			}

			ig.Emit (opcode);
		}

		// <summary>
		//   Constant expression reducer for binary operations
		// </summary>
		public override Expression Reduce (EmitContext ec)
		{
			Console.WriteLine ("Reduce called");
			
			left = left.Reduce (ec);
			right = right.Reduce (ec);

			if (!(left is Literal && right is Literal))
				return this;

			if (method == TypeManager.string_concat_string_string){
				StringLiteral ls = (StringLiteral) left;
				StringLiteral rs = (StringLiteral) right;
				
				return new StringLiteral (ls.Value + rs.Value);
			}

			// FINISH ME.
			
			return this;
		}
	}

	public class Conditional : Expression {
		Expression expr, trueExpr, falseExpr;
		Location loc;
		
		public Conditional (Expression expr, Expression trueExpr, Expression falseExpr, Location l)
		{
			this.expr = expr;
			this.trueExpr = trueExpr;
			this.falseExpr = falseExpr;
			this.loc = l;
		}

		public Expression Expr {
			get {
				return expr;
			}
		}

		public Expression TrueExpr {
			get {
				return trueExpr;
			}
		}

		public Expression FalseExpr {
			get {
				return falseExpr;
			}
		}

		public override Expression DoResolve (EmitContext ec)
		{
			expr = expr.Resolve (ec);

			if (expr.Type != TypeManager.bool_type)
				expr = Expression.ConvertImplicitRequired (
					ec, expr, TypeManager.bool_type, loc);
			
			trueExpr = trueExpr.Resolve (ec);
			falseExpr = falseExpr.Resolve (ec);

			if (expr == null || trueExpr == null || falseExpr == null)
				return null;

			if (trueExpr.Type == falseExpr.Type)
				type = trueExpr.Type;
			else {
				Expression conv;

				//
				// First, if an implicit conversion exists from trueExpr
				// to falseExpr, then the result type is of type falseExpr.Type
				//
				conv = ConvertImplicit (ec, trueExpr, falseExpr.Type, loc);
				if (conv != null){
					type = falseExpr.Type;
					trueExpr = conv;
				} else if ((conv = ConvertImplicit(ec, falseExpr,trueExpr.Type,loc))!= null){
					type = trueExpr.Type;
					falseExpr = conv;
				} else {
					Error (173, loc, "The type of the conditional expression can " +
					       "not be computed because there is no implicit conversion" +
					       " from `" + TypeManager.CSharpName (trueExpr.Type) + "'" +
					       " and `" + TypeManager.CSharpName (falseExpr.Type) + "'");
					return null;
				}
			}

			if (expr is BoolLiteral){
				BoolLiteral bl = (BoolLiteral) expr;

				if (bl.Value)
					return trueExpr;
				else
					return falseExpr;
			}
			
			eclass = ExprClass.Value;
			return this;
		}

		public override void Emit (EmitContext ec)
		{
			ILGenerator ig = ec.ig;
			Label false_target = ig.DefineLabel ();
			Label end_target = ig.DefineLabel ();

			expr.Emit (ec);
			ig.Emit (OpCodes.Brfalse, false_target);
			trueExpr.Emit (ec);
			ig.Emit (OpCodes.Br, end_target);
			ig.MarkLabel (false_target);
			falseExpr.Emit (ec);
			ig.MarkLabel (end_target);
		}

		public override Expression Reduce (EmitContext ec)
		{
			expr = expr.Reduce (ec);
			trueExpr = trueExpr.Reduce (ec);
			falseExpr = falseExpr.Reduce (ec);

			if (!(expr is Literal && trueExpr is Literal && falseExpr is Literal))
				return this;

			BoolLiteral bl = (BoolLiteral) expr;

			if (bl.Value)
				return trueExpr;
			else
				return falseExpr;
		}
	}

	public class LocalVariableReference : Expression, IStackStore, IMemoryLocation {
		public readonly string Name;
		public readonly Block Block;

		VariableInfo variable_info;
		
		public LocalVariableReference (Block block, string name)
		{
			Block = block;
			Name = name;
			eclass = ExprClass.Variable;
		}

		public VariableInfo VariableInfo {
			get {
				if (variable_info == null)
					variable_info = Block.GetVariableInfo (Name);
				return variable_info;
			}
		}
		
		public override Expression DoResolve (EmitContext ec)
		{
			VariableInfo vi = VariableInfo;

			type = vi.VariableType;
			return this;
		}

		public override void Emit (EmitContext ec)
		{
			VariableInfo vi = VariableInfo;
			ILGenerator ig = ec.ig;
			int idx = vi.Idx;

			vi.Used = true;

			switch (idx){
			case 0:
				ig.Emit (OpCodes.Ldloc_0);
				break;
				
			case 1:
				ig.Emit (OpCodes.Ldloc_1);
				break;
				
			case 2:
				ig.Emit (OpCodes.Ldloc_2);
				break;
				
			case 3:
				ig.Emit (OpCodes.Ldloc_3);
				break;
				
			default:
				if (idx <= 255)
					ig.Emit (OpCodes.Ldloc_S, (byte) idx);
				else
					ig.Emit (OpCodes.Ldloc, idx);
				break;
			}
		}
		
		public static void Store (ILGenerator ig, int idx)
		{
			switch (idx){
			case 0:
				ig.Emit (OpCodes.Stloc_0);
				break;
				
			case 1:
				ig.Emit (OpCodes.Stloc_1);
				break;
				
			case 2:
				ig.Emit (OpCodes.Stloc_2);
				break;
				
			case 3:
				ig.Emit (OpCodes.Stloc_3);
				break;
				
			default:
				if (idx <= 255)
					ig.Emit (OpCodes.Stloc_S, (byte) idx);
				else
					ig.Emit (OpCodes.Stloc, idx);
				break;
			}
		}
		
		public void Store (EmitContext ec)
		{
			ILGenerator ig = ec.ig;
			VariableInfo vi = VariableInfo;

			vi.Assigned = true;

			// Funny seems the above generates optimal code for us, but
			// seems to take too long to generate what we need.
			// ig.Emit (OpCodes.Stloc, vi.LocalBuilder);

			Store (ig, vi.Idx);
		}

		public void AddressOf (EmitContext ec)
		{
			VariableInfo vi = VariableInfo;
			int idx = vi.Idx;

			vi.Used = true;
			vi.Assigned = true;
			
			if (idx <= 255)
				ec.ig.Emit (OpCodes.Ldloca_S, (byte) idx);
			else
				ec.ig.Emit (OpCodes.Ldloca, idx);
		}
	}

	public class ParameterReference : Expression, IStackStore, IMemoryLocation {
		public readonly Parameters Pars;
		public readonly String Name;
		public readonly int Idx;
		int arg_idx;
		
		public ParameterReference (Parameters pars, int idx, string name)
		{
			Pars = pars;
			Idx  = idx;
			Name = name;
			eclass = ExprClass.Variable;
		}

		public override Expression DoResolve (EmitContext ec)
		{
			Type [] types = Pars.GetParameterInfo (ec.TypeContainer);

			type = types [Idx];

			arg_idx = Idx;
			if (!ec.IsStatic)
				arg_idx++;
			
			return this;
		}

		public override void Emit (EmitContext ec)
		{
			if (arg_idx <= 255)
				ec.ig.Emit (OpCodes.Ldarg_S, (byte) arg_idx);
			else
				ec.ig.Emit (OpCodes.Ldarg, arg_idx);
		}

		public void Store (EmitContext ec)
		{
			if (arg_idx <= 255)
				ec.ig.Emit (OpCodes.Starg_S, (byte) arg_idx);
			else
				ec.ig.Emit (OpCodes.Starg, arg_idx);
			
		}

		public void AddressOf (EmitContext ec)
		{
			if (arg_idx <= 255)
				ec.ig.Emit (OpCodes.Ldarga_S, (byte) arg_idx);
			else
				ec.ig.Emit (OpCodes.Ldarga, arg_idx);
		}
	}
	
	// <summary>
	//   Used for arguments to New(), Invocation()
	// </summary>
	public class Argument {
		public enum AType {
			Expression,
			Ref,
			Out
		};

		public readonly AType ArgType;
		public Expression expr;

		public Argument (Expression expr, AType type)
		{
			this.expr = expr;
			this.ArgType = type;
		}

		public Expression Expr {
			get {
				return expr;
			}

			set {
				expr = value;
			}
		}

		public Type Type {
			get {
				return expr.Type;
			}
		}

		public Parameter.Modifier GetParameterModifier ()
		{
			if (ArgType == AType.Ref)
				return Parameter.Modifier.REF;

			if (ArgType == AType.Out)
				return Parameter.Modifier.OUT;

			return Parameter.Modifier.NONE;
		}

	        public static string FullDesc (Argument a)
		{
			StringBuilder sb = new StringBuilder ();

			if (a.ArgType == AType.Ref)
				sb.Append ("ref ");

			if (a.ArgType == AType.Out)
				sb.Append ("out ");

			sb.Append (TypeManager.CSharpName (a.Expr.Type));

			return sb.ToString ();
		}
		
		public bool Resolve (EmitContext ec)
		{
			expr = expr.Resolve (ec);

			return expr != null;
		}

		public void Emit (EmitContext ec)
		{
			expr.Emit (ec);
		}
	}

	// <summary>
	//   Invocation of methods or delegates.
	// </summary>
	public class Invocation : ExpressionStatement {
		public readonly ArrayList Arguments;
		public readonly Location Location;

		Expression expr;
		MethodBase method = null;
			
		static Hashtable method_parameter_cache;

		static Invocation ()
		{
			method_parameter_cache = new Hashtable ();
		}
			
		//
		// arguments is an ArrayList, but we do not want to typecast,
		// as it might be null.
		//
		// FIXME: only allow expr to be a method invocation or a
		// delegate invocation (7.5.5)
		//
		public Invocation (Expression expr, ArrayList arguments, Location l)
		{
			this.expr = expr;
			Arguments = arguments;
			Location = l;
		}

		public Expression Expr {
			get {
				return expr;
			}
		}

		// <summary>
		//   Returns the Parameters (a ParameterData interface) for the
		//   Method `mb'
		// </summary>
		public static ParameterData GetParameterData (MethodBase mb)
		{
			object pd = method_parameter_cache [mb];
			object ip;
			
			if (pd != null)
				return (ParameterData) pd;

			
			ip = TypeContainer.LookupParametersByBuilder (mb);
			if (ip != null){
				method_parameter_cache [mb] = ip;

				return (ParameterData) ip;
			} else {
				ParameterInfo [] pi = mb.GetParameters ();
				ReflectionParameters rp = new ReflectionParameters (pi);
				method_parameter_cache [mb] = rp;

				return (ParameterData) rp;
			}
		}

		// <summary>
		//   Tells whether a user defined conversion from Type `from' to
		//   Type `to' exists.
		//
		//   FIXME: we could implement a cache here. 
		// </summary>
		static bool ConversionExists (EmitContext ec, Type from, Type to, Location loc)
		{
			// Locate user-defined implicit operators

			Expression mg;
			
			mg = MemberLookup (ec, to, "op_Implicit", false, loc);

			if (mg != null) {
				MethodGroupExpr me = (MethodGroupExpr) mg;
				
				for (int i = me.Methods.Length; i > 0;) {
					i--;
					MethodBase mb = me.Methods [i];
					ParameterData pd = GetParameterData (mb);
					
					if (from == pd.ParameterType (0))
						return true;
				}
			}

			mg = MemberLookup (ec, from, "op_Implicit", false, loc);

			if (mg != null) {
				MethodGroupExpr me = (MethodGroupExpr) mg;

				for (int i = me.Methods.Length; i > 0;) {
					i--;
					MethodBase mb = me.Methods [i];
					MethodInfo mi = (MethodInfo) mb;
					
					if (mi.ReturnType == to)
						return true;
				}
			}
			
			return false;
		}
		
		// <summary>
		//  Determines "better conversion" as specified in 7.4.2.3
		//  Returns : 1 if a->p is better
		//            0 if a->q or neither is better 
		// </summary>
		static int BetterConversion (EmitContext ec, Argument a, Type p, Type q, bool use_standard,
					     Location loc)
		{
			Type argument_type = a.Type;
			Expression argument_expr = a.Expr;

			if (argument_type == null)
				throw new Exception ("Expression of type " + a.Expr + " does not resolve its type");

			if (p == q)
				return 0;
			
			if (argument_type == p)
				return 1;

			if (argument_type == q)
				return 0;

			//
			// Now probe whether an implicit constant expression conversion
			// can be used.
			//
			// An implicit constant expression conversion permits the following
			// conversions:
			//
			//    * A constant-expression of type `int' can be converted to type
			//      sbyte, byute, short, ushort, uint, ulong provided the value of
			//      of the expression is withing the range of the destination type.
			//
			//    * A constant-expression of type long can be converted to type
			//      ulong, provided the value of the constant expression is not negative
			//
			// FIXME: Note that this assumes that constant folding has
			// taken place.  We dont do constant folding yet.
			//

			if (argument_expr is IntLiteral){
				IntLiteral ei = (IntLiteral) argument_expr;
				int value = ei.Value;
				
				if (p == TypeManager.sbyte_type){
					if (value >= SByte.MinValue && value <= SByte.MaxValue)
						return 1;
				} else if (p == TypeManager.byte_type){
					if (Byte.MinValue >= 0 && value <= Byte.MaxValue)
						return 1;
				} else if (p == TypeManager.short_type){
					if (value >= Int16.MinValue && value <= Int16.MaxValue)
						return 1;
				} else if (p == TypeManager.ushort_type){
					if (value >= UInt16.MinValue && value <= UInt16.MaxValue)
						return 1;
				} else if (p == TypeManager.uint32_type){
					//
					// we can optimize this case: a positive int32
					// always fits on a uint32
					//
					if (value >= 0)
						return 1;
				} else if (p == TypeManager.uint64_type){
					//
					// we can optimize this case: a positive int32
					// always fits on a uint64
					//
					if (value >= 0)
						return 1;
				}
			} else if (argument_type == TypeManager.int64_type && argument_expr is LongLiteral){
				LongLiteral ll = (LongLiteral) argument_expr;
				
				if (p == TypeManager.uint64_type){
					if (ll.Value > 0)
						return 1;
				}
			}

			if (q == null) {

				Expression tmp;

				if (use_standard)
					tmp = ConvertImplicitStandard (ec, argument_expr, p, loc);
				else
					tmp = ConvertImplicit (ec, argument_expr, p, loc);

				if (tmp != null)
					return 1;
				else
					return 0;

			}

			if (ConversionExists (ec, p, q, loc) == true &&
			    ConversionExists (ec, q, p, loc) == false)
				return 1;

			if (p == TypeManager.sbyte_type)
				if (q == TypeManager.byte_type || q == TypeManager.ushort_type ||
				    q == TypeManager.uint32_type || q == TypeManager.uint64_type)
					return 1;

			if (p == TypeManager.short_type)
				if (q == TypeManager.ushort_type || q == TypeManager.uint32_type ||
				    q == TypeManager.uint64_type)
					return 1;

			if (p == TypeManager.int32_type)
				if (q == TypeManager.uint32_type || q == TypeManager.uint64_type)
					return 1;

			if (p == TypeManager.int64_type)
				if (q == TypeManager.uint64_type)
					return 1;

			return 0;
		}
		
		// <summary>
		//  Determines "Better function" and returns an integer indicating :
		//  0 if candidate ain't better
		//  1 if candidate is better than the current best match
		// </summary>
		static int BetterFunction (EmitContext ec, ArrayList args,
					   MethodBase candidate, MethodBase best,
					   bool use_standard, Location loc)
		{
			ParameterData candidate_pd = GetParameterData (candidate);
			ParameterData best_pd;
			int argument_count;

			if (args == null)
				argument_count = 0;
			else
				argument_count = args.Count;

			if (candidate_pd.Count == 0 && argument_count == 0)
				return 1;

			if (best == null) {
				if (candidate_pd.Count == argument_count) {
					int x = 0;
					for (int j = argument_count; j > 0;) {
						j--;
						
						Argument a = (Argument) args [j];
						
						x = BetterConversion (
							ec, a, candidate_pd.ParameterType (j), null,
							use_standard, loc);
						
						if (x <= 0)
							break;
					}
					
					if (x > 0)
						return 1;
					else
						return 0;
					
				} else
					return 0;
			}

			best_pd = GetParameterData (best);

			if (candidate_pd.Count == argument_count && best_pd.Count == argument_count) {
				int rating1 = 0, rating2 = 0;
				
				for (int j = argument_count; j > 0;) {
					j--;
					int x, y;
					
					Argument a = (Argument) args [j];

					x = BetterConversion (ec, a, candidate_pd.ParameterType (j),
							      best_pd.ParameterType (j), use_standard, loc);
					y = BetterConversion (ec, a, best_pd.ParameterType (j),
							      candidate_pd.ParameterType (j), use_standard,
							      loc);
					
					rating1 += x;
					rating2 += y;
				}

				if (rating1 > rating2)
					return 1;
				else
					return 0;
			} else
				return 0;
			
		}

		public static string FullMethodDesc (MethodBase mb)
		{
			StringBuilder sb = new StringBuilder (mb.Name);
			ParameterData pd = GetParameterData (mb);

			int count = pd.Count;
			sb.Append (" (");
			
			for (int i = count; i > 0; ) {
				i--;
				
				sb.Append (pd.ParameterDesc (count - i - 1));
				if (i != 0)
					sb.Append (", ");
			}
			
			sb.Append (")");
			return sb.ToString ();
		}

		public static MethodGroupExpr MakeUnionSet (Expression mg1, Expression mg2)
		{
			MemberInfo [] miset;
			MethodGroupExpr union;
			
			if (mg1 != null && mg2 != null) {
				
				MethodGroupExpr left_set = null, right_set = null;
				int length1 = 0, length2 = 0;
				
				left_set = (MethodGroupExpr) mg1;
				length1 = left_set.Methods.Length;
				
				right_set = (MethodGroupExpr) mg2;
				length2 = right_set.Methods.Length;

				ArrayList common = new ArrayList ();
				
				for (int i = 0; i < left_set.Methods.Length; i++) {
					for (int j = 0; j < right_set.Methods.Length; j++) {
						if (left_set.Methods [i] == right_set.Methods [j]) 
							common.Add (left_set.Methods [i]);
					}
				}
				
				miset = new MemberInfo [length1 + length2 - common.Count];

				left_set.Methods.CopyTo (miset, 0);

				int k = 0;
				
				for (int j = 0; j < right_set.Methods.Length; j++)
					if (!common.Contains (right_set.Methods [j]))
						miset [length1 + k++] = right_set.Methods [j];
				
				union = new MethodGroupExpr (miset);

				return union;

			} else if (mg1 == null && mg2 != null) {
				
				MethodGroupExpr me = (MethodGroupExpr) mg2; 
				
				miset = new MemberInfo [me.Methods.Length];
				me.Methods.CopyTo (miset, 0);

				union = new MethodGroupExpr (miset);
				
				return union;

			} else if (mg2 == null && mg1 != null) {
				
				MethodGroupExpr me = (MethodGroupExpr) mg1; 
				
				miset = new MemberInfo [me.Methods.Length];
				me.Methods.CopyTo (miset, 0);

				union = new MethodGroupExpr (miset);
				
				return union;
			}
			
			return null;
		}

		// <summary>
		//  Determines is the candidate method, if a params method, is applicable
		//  in its expanded form to the given set of arguments
		// </summary>
		static bool IsParamsMethodApplicable (ArrayList arguments, MethodBase candidate)
		{
			int arg_count;
			
			if (arguments == null)
				arg_count = 0;
			else
				arg_count = arguments.Count;
			
			ParameterData pd = GetParameterData (candidate);
			
			int pd_count = pd.Count;

			if (pd.ParameterModifier (pd_count - 1) != Parameter.Modifier.PARAMS)
				return false;

			if (pd_count - 1 > arg_count)
				return false;

			// If we have come this far, the case which remains is when the number of parameters
			// is less than or equal to the argument count. So, we now check if the element type
			// of the params array is compatible with each argument type
			//

			Type element_type = pd.ParameterType (pd_count - 1).GetElementType ();

			for (int i = pd_count - 1; i < arg_count - 1; i++) {
				Argument a = (Argument) arguments [i];
				if (!StandardConversionExists (a.Type, element_type))
					return false;
			}
			
			return true;
		}

		// <summary>
		//  Determines if the candidate method is applicable (section 14.4.2.1)
		//  to the given set of arguments
		// </summary>
		static bool IsApplicable (ArrayList arguments, MethodBase candidate)
		{
			int arg_count;

			if (arguments == null)
				arg_count = 0;
			else
				arg_count = arguments.Count;

			ParameterData pd = GetParameterData (candidate);

			int pd_count = pd.Count;

			if (arg_count != pd.Count)
				return false;

			for (int i = arg_count; i > 0; ) {
				i--;

				Argument a = (Argument) arguments [i];

				Parameter.Modifier a_mod = a.GetParameterModifier ();
				Parameter.Modifier p_mod = pd.ParameterModifier (i);

				if (a_mod == p_mod) {
					
					if (a_mod == Parameter.Modifier.NONE)
						if (!StandardConversionExists (a.Type, pd.ParameterType (i)))
							return false;
					
					if (a_mod == Parameter.Modifier.REF ||
					    a_mod == Parameter.Modifier.OUT)
						if (pd.ParameterType (i) != a.Type)
							return false;
				} else
					return false;
			}

			return true;
		}
		
		

		// <summary>
		//   Find the Applicable Function Members (7.4.2.1)
		//
		//   me: Method Group expression with the members to select.
		//       it might contain constructors or methods (or anything
		//       that maps to a method).
		//
		//   Arguments: ArrayList containing resolved Argument objects.
		//
		//   loc: The location if we want an error to be reported, or a Null
		//        location for "probing" purposes.
		//
		//   use_standard: controls whether OverloadResolve should use the 
		//   ConvertImplicit or ConvertImplicitStandard during overload resolution.
		//
		//   Returns: The MethodBase (either a ConstructorInfo or a MethodInfo)
		//            that is the best match of me on Arguments.
		//
		// </summary>
		public static MethodBase OverloadResolve (EmitContext ec, MethodGroupExpr me,
							  ArrayList Arguments, Location loc,
							  bool use_standard)
		{
			ArrayList afm = new ArrayList ();
			int best_match_idx = -1;
			MethodBase method = null;
			int argument_count;
			
			for (int i = me.Methods.Length; i > 0; ){
				i--;
				MethodBase candidate  = me.Methods [i];
				int x;

				// Check if candidate is applicable (section 14.4.2.1)
				if (!IsApplicable (Arguments, candidate))
					continue;

				x = BetterFunction (ec, Arguments, candidate, method, use_standard, loc);
				
				if (x == 0)
					continue;
				else {
					best_match_idx = i;
					method = me.Methods [best_match_idx];
				}
			}

			if (Arguments == null)
				argument_count = 0;
			else
				argument_count = Arguments.Count;

			//
			// Now we see if we can find params functions, applicable in their expanded form
			// since if they were applicable in their normal form, they would have been selected
			// above anyways
			//
			if (best_match_idx == -1) {

				for (int i = me.Methods.Length; i > 0; ) {
					i--;
					MethodBase candidate = me.Methods [i];

					if (IsParamsMethodApplicable (Arguments, candidate)) {
						best_match_idx = i;
						method = me.Methods [best_match_idx];
						break;
					}
				}
			}

			//
			// Now we see if we can at least find a method with the same number of arguments
			//
			ParameterData pd;
			
			if (best_match_idx == -1) {
				
				for (int i = me.Methods.Length; i > 0;) {
					i--;
					MethodBase mb = me.Methods [i];
					pd = GetParameterData (mb);
					
					if (pd.Count == argument_count) {
						best_match_idx = i;
						method = me.Methods [best_match_idx];
						break;
					} else
						continue;
				}
			}

			if (method == null)
				return null;
			
			// And now convert implicitly, each argument to the required type
			
			pd = GetParameterData (method);
			int pd_count = pd.Count;

			for (int j = 0; j < argument_count; j++) {

				Argument a = (Argument) Arguments [j];
				Expression a_expr = a.Expr;
				Type parameter_type = pd.ParameterType (j);

				//
				// Note that we need to compare against the element type
				// when we have a params method
				//
				if (pd.ParameterModifier (pd_count - 1) == Parameter.Modifier.PARAMS) {
					if (j >= pd_count - 1) 
						parameter_type = pd.ParameterType (pd_count - 1).GetElementType ();
				}

				if (a.Type != parameter_type){
					Expression conv;
					
					if (use_standard)
						conv = ConvertImplicitStandard (ec, a_expr, parameter_type,
										Location.Null);
					else
						conv = ConvertImplicit (ec, a_expr, parameter_type,
									Location.Null);

					if (conv == null) {
						if (!Location.IsNull (loc)) {
							Error (1502, loc,
						        "The best overloaded match for method '" + FullMethodDesc (method)+
							       "' has some invalid arguments");
							Error (1503, loc,
							 "Argument " + (j+1) +
							 ": Cannot convert from '" + Argument.FullDesc (a) 
							 + "' to '" + pd.ParameterDesc (j) + "'");
						}
						return null;
					}
					
			
					
					//
					// Update the argument with the implicit conversion
					//
					if (a_expr != conv)
						a.Expr = conv;

					// FIXME : For the case of params methods, we need to actually instantiate
					// an array and initialize it with the argument values etc etc.

				}
				
				if (a.GetParameterModifier () != pd.ParameterModifier (j) &&
				    pd.ParameterModifier (j) != Parameter.Modifier.PARAMS) {
					if (!Location.IsNull (loc)) {
						Error (1502, loc,
						       "The best overloaded match for method '" + FullMethodDesc (method)+
						       "' has some invalid arguments");
						Error (1503, loc,
						       "Argument " + (j+1) +
						       ": Cannot convert from '" + Argument.FullDesc (a) 
						       + "' to '" + pd.ParameterDesc (j) + "'");
					}
					return null;
				}
				
				
			}
			
			return method;
		}
		
		public static MethodBase OverloadResolve (EmitContext ec, MethodGroupExpr me,
							  ArrayList Arguments, Location loc)
		{
			return OverloadResolve (ec, me, Arguments, loc, false);
		}
			
		public override Expression DoResolve (EmitContext ec)
		{
			//
			// First, resolve the expression that is used to
			// trigger the invocation
			//
			expr = expr.Resolve (ec);
			if (expr == null)
				return null;

			if (!(expr is MethodGroupExpr)) {
				Type expr_type = expr.Type;

				if (expr_type != null){
					bool IsDelegate = TypeManager.IsDelegateType (expr_type);
					if (IsDelegate)
						return (new DelegateInvocation (
							this.expr, Arguments, Location)).Resolve (ec);
				}
			}

			if (!(expr is MethodGroupExpr)){
				report118 (Location, this.expr, "method group");
				return null;
			}

			//
			// Next, evaluate all the expressions in the argument list
			//
			if (Arguments != null){
				for (int i = Arguments.Count; i > 0;){
					--i;
					Argument a = (Argument) Arguments [i];

					if (!a.Resolve (ec))
						return null;
				}
			}

			method = OverloadResolve (ec, (MethodGroupExpr) this.expr, Arguments,
						  Location);

			if (method == null){
				Error (-6, Location,
				       "Could not find any applicable function for this argument list");
				return null;
			}

			if (method is MethodInfo)
				type = ((MethodInfo)method).ReturnType;

			eclass = ExprClass.Value;
			return this;
		}

		public static void EmitArguments (EmitContext ec, ArrayList Arguments)
		{
			int top;

			if (Arguments != null)
				top = Arguments.Count;
			else
				top = 0;

			for (int i = 0; i < top; i++){
				Argument a = (Argument) Arguments [i];

				a.Emit (ec);
			}
		}

		public static void EmitCall (EmitContext ec,
					     bool is_static, Expression instance_expr,
					     MethodBase method, ArrayList Arguments)
		{
			ILGenerator ig = ec.ig;
			bool struct_call = false;
				
			if (!is_static){
				//
				// If this is ourselves, push "this"
				//
				if (instance_expr == null){
					ig.Emit (OpCodes.Ldarg_0);
				} else {
					//
					// Push the instance expression
					//
					if (instance_expr.Type.IsSubclassOf (TypeManager.value_type)){

						struct_call = true;

						//
						// If the expression implements IMemoryLocation, then
						// we can optimize and use AddressOf on the
						// return.
						//
						// If not we have to use some temporary storage for
						// it.
						if (instance_expr is IMemoryLocation)
							((IMemoryLocation) instance_expr).AddressOf (ec);
						else {
							Type t = instance_expr.Type;
							
							instance_expr.Emit (ec);
							LocalBuilder temp = ec.GetTemporaryStorage (t);
							ig.Emit (OpCodes.Stloc, temp);
							ig.Emit (OpCodes.Ldloca, temp);
						}
					} else 
						instance_expr.Emit (ec);
				}
			}

			if (Arguments != null)
				EmitArguments (ec, Arguments);

			if (is_static || struct_call){
				if (method is MethodInfo)
					ig.Emit (OpCodes.Call, (MethodInfo) method);
				else
					ig.Emit (OpCodes.Call, (ConstructorInfo) method);
			} else {
				if (method is MethodInfo)
					ig.Emit (OpCodes.Callvirt, (MethodInfo) method);
				else
					ig.Emit (OpCodes.Callvirt, (ConstructorInfo) method);
			}
		}
		
		public override void Emit (EmitContext ec)
		{
			MethodGroupExpr mg = (MethodGroupExpr) this.expr;
			EmitCall (ec, method.IsStatic, mg.InstanceExpression, method, Arguments);
		}
		
		public override void EmitStatement (EmitContext ec)
		{
			Emit (ec);

			// 
			// Pop the return value if there is one
			//
			if (method is MethodInfo){
				if (((MethodInfo)method).ReturnType != TypeManager.void_type)
					ec.ig.Emit (OpCodes.Pop);
			}
		}
	}

	public class New : ExpressionStatement {
		public readonly ArrayList Arguments;
		public readonly string    RequestedType;

		Location Location;
		MethodBase method = null;

		//
		// If set, the new expression is for a value_target, and
		// we will not leave anything on the stack.
		//
		Expression value_target;
		
		public New (string requested_type, ArrayList arguments, Location loc)
		{
			RequestedType = requested_type;
			Arguments = arguments;
			Location = loc;
		}

		public Expression ValueTypeVariable {
			get {
				return value_target;
			}

			set {
				value_target = value;
			}
		}

		public override Expression DoResolve (EmitContext ec)
		{
			type = ec.TypeContainer.LookupType (RequestedType, false);
			
			if (type == null)
				return null;
			
			bool IsDelegate = TypeManager.IsDelegateType (type);
			
			if (IsDelegate)
				return (new NewDelegate (type, Arguments, Location)).Resolve (ec);
			
			Expression ml;
			
			ml = MemberLookup (ec, type, ".ctor", false,
					   MemberTypes.Constructor, AllBindingsFlags, Location);
			
			bool is_struct = false;
			is_struct = type.IsSubclassOf (TypeManager.value_type);
			
			if (! (ml is MethodGroupExpr)){
				if (!is_struct){
					report118 (Location, ml, "method group");
					return null;
				}
			}
			
			if (ml != null) {
				if (Arguments != null){
					for (int i = Arguments.Count; i > 0;){
						--i;
						Argument a = (Argument) Arguments [i];
						
						if (!a.Resolve (ec))
							return null;
					}
				}

				method = Invocation.OverloadResolve (ec, (MethodGroupExpr) ml,
								     Arguments, Location);
			}
			
			if (method == null && !is_struct) {
				Error (-6, Location,
				       "New invocation: Can not find a constructor for " +
				       "this argument list");
				return null;
			}
			
			eclass = ExprClass.Value;
			return this;
		}

		//
		// This DoEmit can be invoked in two contexts:
		//    * As a mechanism that will leave a value on the stack (new object)
		//    * As one that wont (init struct)
		//
		// You can control whether a value is required on the stack by passing
		// need_value_on_stack.  The code *might* leave a value on the stack
		// so it must be popped manually
		//
		// Returns whether a value is left on the stack
		//
		bool DoEmit (EmitContext ec, bool need_value_on_stack)
		{
			if (method == null){
				IMemoryLocation ml = (IMemoryLocation) value_target;

				ml.AddressOf (ec);
			} else {
				Invocation.EmitArguments (ec, Arguments);
				ec.ig.Emit (OpCodes.Newobj, (ConstructorInfo) method);
				return true;
			}

			//
			// It must be a value type, sanity check
			//
			if (value_target != null){
				ec.ig.Emit (OpCodes.Initobj, type);

				if (need_value_on_stack){
					value_target.Emit (ec);
					return true;
				}
				return false;
			}

			throw new Exception ("No method and no value type");
		}

		public override void Emit (EmitContext ec)
		{
			DoEmit (ec, true);
		}
		
		public override void EmitStatement (EmitContext ec)
		{
			if (DoEmit (ec, false))
				ec.ig.Emit (OpCodes.Pop);
		}
	}

	// <summary>
	//   Represents an array creation expression.
	// </summary>
	//
	// <remarks>
	//   There are two possible scenarios here: one is an array creation
	//   expression that specifies the dimensions and optionally the
	//   initialization data
	// </remarks>
	public class ArrayCreation : ExpressionStatement {

		string RequestedType;
		string Rank;
		ArrayList Initializers;
		Location  Location;
		ArrayList Arguments;

		MethodBase method = null;
		Type array_element_type;
		bool IsOneDimensional = false;
		
		bool IsBuiltinType = false;

		public ArrayCreation (string requested_type, ArrayList exprs,
				      string rank, ArrayList initializers, Location l)
		{
			RequestedType = requested_type;
			Rank          = rank;
			Initializers  = initializers;
			Location      = l;

			Arguments = new ArrayList ();

			foreach (Expression e in exprs)
				Arguments.Add (new Argument (e, Argument.AType.Expression));
			
		}

		public ArrayCreation (string requested_type, string rank, ArrayList initializers, Location l)
		{
			RequestedType = requested_type;
			Rank = rank;
			Initializers = initializers;
			Location = l;
		}

		public static string FormArrayType (string base_type, int idx_count, string rank)
		{
			StringBuilder sb = new StringBuilder (base_type);

			sb.Append (rank);
			
			sb.Append ("[");
			for (int i = 1; i < idx_count; i++)
				sb.Append (",");
			sb.Append ("]");
			
			return sb.ToString ();
                }

		public static string FormElementType (string base_type, int idx_count, string rank)
		{
			StringBuilder sb = new StringBuilder (base_type);
			
			sb.Append ("[");
			for (int i = 1; i < idx_count; i++)
				sb.Append (",");
			sb.Append ("]");

			sb.Append (rank);

			string val = sb.ToString ();

			return val.Substring (0, val.LastIndexOf ("["));
		}
		

		public override Expression DoResolve (EmitContext ec)
		{
			int arg_count;
			
			if (Arguments == null)
				arg_count = 0;
			else
				arg_count = Arguments.Count;
			
			string array_type = FormArrayType (RequestedType, arg_count, Rank);

			string element_type = FormElementType (RequestedType, arg_count, Rank);

			type = ec.TypeContainer.LookupType (array_type, false);
			
			array_element_type = ec.TypeContainer.LookupType (element_type, false);
			
			if (type == null)
				return null;
			
			if (arg_count == 1) {
				IsOneDimensional = true;
				eclass = ExprClass.Value;
				return this;
			}

			IsBuiltinType = TypeManager.IsBuiltinType (type);
			
			if (IsBuiltinType) {
				
				Expression ml;
				
				ml = MemberLookup (ec, type, ".ctor", false, MemberTypes.Constructor,
						   AllBindingsFlags, Location);
				
				if (!(ml is MethodGroupExpr)){
					report118 (Location, ml, "method group");
					return null;
				}
				
				if (ml == null) {
					Report.Error (-6, Location, "New invocation: Can not find a constructor for " +
						      "this argument list");
					return null;
				}
				
				if (Arguments != null) {
					for (int i = arg_count; i > 0;){
						--i;
						Argument a = (Argument) Arguments [i];
						
						if (!a.Resolve (ec))
							return null;
					}
				}
				
				method = Invocation.OverloadResolve (ec, (MethodGroupExpr) ml, Arguments, Location);
				
				if (method == null) {
					Report.Error (-6, Location, "New invocation: Can not find a constructor for " +
						      "this argument list");
					return null;
				}
				
				eclass = ExprClass.Value;
				return this;
				
			} else {

				ModuleBuilder mb = ec.TypeContainer.RootContext.ModuleBuilder;

				ArrayList args = new ArrayList ();
				if (Arguments != null){
					for (int i = arg_count; i > 0;){
						--i;
						Argument a = (Argument) Arguments [i];
						
						if (!a.Resolve (ec))
							return null;
						
						args.Add (a.Type);
					}
				}
				
				Type [] arg_types = null;
				
				if (args.Count > 0)
					arg_types = new Type [args.Count];
				
				args.CopyTo (arg_types, 0);
				
				method = mb.GetArrayMethod (type, ".ctor", CallingConventions.HasThis, null,
							    arg_types);
				
				if (method == null) {
					Report.Error (-6, Location, "New invocation: Can not find a constructor for " +
						      "this argument list");
					return null;
				}
				
				eclass = ExprClass.Value;
				return this;
				
			}
		}

		public override void Emit (EmitContext ec)
		{
			ILGenerator ig = ec.ig;
			
			if (IsOneDimensional) {
				Invocation.EmitArguments (ec, Arguments);
				ig.Emit (OpCodes.Newarr, array_element_type);
				
			} else {
				Invocation.EmitArguments (ec, Arguments);

				if (IsBuiltinType)
					ig.Emit (OpCodes.Newobj, (ConstructorInfo) method);
				else
					ig.Emit (OpCodes.Newobj, (MethodInfo) method);
			}

			if (Initializers != null){
				FieldBuilder fb;

				// FIXME: This is just sample data, need to fill with
				// real values.
				byte [] a = new byte [4] { 1, 2, 3, 4 };
				
				fb = ec.TypeContainer.RootContext.MakeStaticData (a);

				ig.Emit (OpCodes.Dup);
				ig.Emit (OpCodes.Ldtoken, fb);
				ig.Emit (OpCodes.Call, TypeManager.void_initializearray_array_fieldhandle);
			}
		}
		
		public override void EmitStatement (EmitContext ec)
		{
			Emit (ec);
			ec.ig.Emit (OpCodes.Pop);
		}
		
	}
	
	//
	// Represents the `this' construct
	//
	public class This : Expression, IStackStore, IMemoryLocation {
		Location loc;
		
		public This (Location loc)
		{
			this.loc = loc;
		}

		public override Expression DoResolve (EmitContext ec)
		{
			eclass = ExprClass.Variable;
			type = ec.TypeContainer.TypeBuilder;

			if (ec.IsStatic){
				Report.Error (26, loc,
					      "Keyword this not valid in static code");
				return null;
			}
			
			return this;
		}

		public Expression DoResolveLValue (EmitContext ec)
		{
			DoResolve (ec);
			
			if (ec.TypeContainer is Class){
				Report.Error (1604, loc, "Cannot assign to `this'");
				return null;
			}

			return this;
		}

		public override void Emit (EmitContext ec)
		{
			ec.ig.Emit (OpCodes.Ldarg_0);
		}

		public void Store (EmitContext ec)
		{
			ec.ig.Emit (OpCodes.Starg, 0);
		}

		public void AddressOf (EmitContext ec)
		{
			ec.ig.Emit (OpCodes.Ldarga_S, (byte) 0);
		}
	}

	// <summary>
	//   Implements the typeof operator
	// </summary>
	public class TypeOf : Expression {
		public readonly string QueriedType;
		Type typearg;
		
		public TypeOf (string queried_type)
		{
			QueriedType = queried_type;
		}

		public override Expression DoResolve (EmitContext ec)
		{
			typearg = ec.TypeContainer.LookupType (QueriedType, false);

			if (typearg == null)
				return null;

			type = TypeManager.type_type;
			eclass = ExprClass.Type;
			return this;
		}

		public override void Emit (EmitContext ec)
		{
			ec.ig.Emit (OpCodes.Ldtoken, typearg);
			ec.ig.Emit (OpCodes.Call, TypeManager.system_type_get_type_from_handle);
		}
	}

	public class SizeOf : Expression {
		public readonly string QueriedType;
		
		public SizeOf (string queried_type)
		{
			this.QueriedType = queried_type;
		}

		public override Expression DoResolve (EmitContext ec)
		{
			// FIXME: Implement;
			throw new Exception ("Unimplemented");
			// return this;
		}

		public override void Emit (EmitContext ec)
		{
			throw new Exception ("Implement me");
		}
	}

	public class MemberAccess : Expression {
		public readonly string Identifier;
		Expression expr;
		Expression member_lookup;
		Location loc;
		
		public MemberAccess (Expression expr, string id, Location l)
		{
			this.expr = expr;
			Identifier = id;
			loc = l;
		}

		public Expression Expr {
			get {
				return expr;
			}
		}

		void error176 (Location loc, string name)
		{
			Report.Error (176, loc, "Static member `" +
				      name + "' cannot be accessed " +
				      "with an instance reference, qualify with a " +
				      "type name instead");
		}
		
		public override Expression DoResolve (EmitContext ec)
		{
			//
			// We are the sole users of ResolveWithSimpleName (ie, the only
			// ones that can cope with it
			//
			expr = expr.ResolveWithSimpleName (ec);

			if (expr == null)
				return null;

			if (expr is SimpleName){
				SimpleName child_expr = (SimpleName) expr;
				
				expr = new SimpleName (child_expr.Name + "." + Identifier, loc);

				return expr.Resolve (ec);
			}
					
			member_lookup = MemberLookup (ec, expr.Type, Identifier, false, loc);

			if (member_lookup == null)
				return null;
			
			//
			// Method Groups
			//
			if (member_lookup is MethodGroupExpr){
				MethodGroupExpr mg = (MethodGroupExpr) member_lookup;
				
				//
				// Type.MethodGroup
				//
				if (expr is TypeExpr){
					if (!mg.RemoveInstanceMethods ()){
						SimpleName.Error120 (loc, mg.Methods [0].Name); 
						return null;
					}

					return member_lookup;
				}

				//
				// Instance.MethodGroup
				//
				if (!mg.RemoveStaticMethods ()){
					error176 (loc, mg.Methods [0].Name);
					return null;
				}
				
				mg.InstanceExpression = expr;
					
				return member_lookup;
			}

			if (member_lookup is FieldExpr){
				FieldExpr fe = (FieldExpr) member_lookup;

				if (expr is TypeExpr){
					if (!fe.FieldInfo.IsStatic){
						error176 (loc, fe.FieldInfo.Name);
						return null;
					}
					return member_lookup;
				} else {
					if (fe.FieldInfo.IsStatic){
						error176 (loc, fe.FieldInfo.Name);
						return null;
					}
					fe.InstanceExpression = expr;

					return fe;
				}
			}

			if (member_lookup is PropertyExpr){
				PropertyExpr pe = (PropertyExpr) member_lookup;

				if (expr is TypeExpr){
					if (!pe.IsStatic){
						SimpleName.Error120 (loc, pe.PropertyInfo.Name);
						return null;
					}
					return pe;
				} else {
					if (pe.IsStatic){
						error176 (loc, pe.PropertyInfo.Name);
						return null;
					}
					pe.InstanceExpression = expr;

					return pe;
				}
			}
			
			Console.WriteLine ("Support for [" + member_lookup + "] is not present yet");
			Environment.Exit (0);
			return null;
		}

		public override void Emit (EmitContext ec)
		{
			throw new Exception ("Should not happen I think");
		}

	}

	public class CheckedExpr : Expression {

		public Expression Expr;

		public CheckedExpr (Expression e)
		{
			Expr = e;
		}

		public override Expression DoResolve (EmitContext ec)
		{
			Expr = Expr.Resolve (ec);

			if (Expr == null)
				return null;

			eclass = Expr.ExprClass;
			type = Expr.Type;
			return this;
		}

		public override void Emit (EmitContext ec)
		{
			bool last_check = ec.CheckState;
			
			ec.CheckState = true;
			Expr.Emit (ec);
			ec.CheckState = last_check;
		}
		
	}

	public class UnCheckedExpr : Expression {

		public Expression Expr;

		public UnCheckedExpr (Expression e)
		{
			Expr = e;
		}

		public override Expression DoResolve (EmitContext ec)
		{
			Expr = Expr.Resolve (ec);

			if (Expr == null)
				return null;

			eclass = Expr.ExprClass;
			type = Expr.Type;
			return this;
		}

		public override void Emit (EmitContext ec)
		{
			bool last_check = ec.CheckState;
			
			ec.CheckState = false;
			Expr.Emit (ec);
			ec.CheckState = last_check;
		}
		
	}

	public class ElementAccess : Expression {
		
		public ArrayList  Arguments;
		public Expression Expr;
		public Location   loc;
		
		public ElementAccess (Expression e, ArrayList e_list, Location l)
		{
			Expr = e;

			Arguments = new ArrayList ();
			foreach (Expression tmp in e_list)
				Arguments.Add (new Argument (tmp, Argument.AType.Expression));
			
			loc  = l;
		}

		bool CommonResolve (EmitContext ec)
		{
			Expr = Expr.Resolve (ec);

			if (Expr == null) 
				return false;

			if (Arguments == null)
				return false;

			for (int i = Arguments.Count; i > 0;){
				--i;
				Argument a = (Argument) Arguments [i];
				
				if (!a.Resolve (ec))
					return false;
			}

			return true;
		}
				
		public override Expression DoResolve (EmitContext ec)
		{
			if (!CommonResolve (ec))
				return null;

			//
			// We perform some simple tests, and then to "split" the emit and store
			// code we create an instance of a different class, and return that.
			//
			// I am experimenting with this pattern.
			//
			if (Expr.Type == TypeManager.array_type)
				return (new ArrayAccess (this)).Resolve (ec);
			else
				return (new IndexerAccess (this)).Resolve (ec);
		}

		public override Expression DoResolveLValue (EmitContext ec, Expression right_side)
		{
			if (!CommonResolve (ec))
				return null;

			if (Expr.Type == TypeManager.array_type)
				return (new ArrayAccess (this)).ResolveLValue (ec, right_side);
			else
				return (new IndexerAccess (this)).ResolveLValue (ec, right_side);
		}
		
		public override void Emit (EmitContext ec)
		{
			throw new Exception ("Should never be reached");
		}
	}

	public class ArrayAccess : Expression, IStackStore {
		//
		// Points to our "data" repository
		//
		ElementAccess ea;
		
		public ArrayAccess (ElementAccess ea_data)
		{
			ea = ea_data;
			eclass = ExprClass.Variable;

			//
			// FIXME: Figure out the type here
			//
		}

		Expression CommonResolve (EmitContext ec)
		{
			return this;
		}
		
		public override Expression DoResolve (EmitContext ec)
		{
			if (ea.Expr.ExprClass != ExprClass.Variable) {
				report118 (ea.loc, ea.Expr, "variable");
				return null;
			}
			
			throw new Exception ("Implement me");
		}

		public void Store (EmitContext ec)
		{
			throw new Exception ("Implement me !");
		}

		public override void Emit (EmitContext ec)
		{
			throw new Exception ("Implement me !");
		}
	}

	class Indexers {
		public ArrayList getters, setters;
		static Hashtable map;

		static Indexers ()
		{
			map = new Hashtable ();
		}

		Indexers (MemberInfo [] mi)
		{
			foreach (PropertyInfo property in mi){
				MethodInfo get, set;
				
				get = property.GetGetMethod (true);
				if (get != null){
					if (getters == null)
						getters = new ArrayList ();

					getters.Add (get);
				}
				
				set = property.GetSetMethod (true);
				if (set != null){
					if (setters == null)
						setters = new ArrayList ();
					setters.Add (set);
				}
			}
		}
		
		static public Indexers GetIndexersForType (Type t, TypeManager tm, Location loc) 
		{
			Indexers ix = (Indexers) map [t];
			string p_name = TypeManager.IndexerPropertyName (t);
			
			if (ix != null)
				return ix;

			MemberInfo [] mi = tm.FindMembers (
				t, MemberTypes.Property,
				BindingFlags.Public | BindingFlags.Instance,
				Type.FilterName, p_name);

			if (mi == null || mi.Length == 0){
				Report.Error (21, loc,
					      "Type `" + TypeManager.CSharpName (t) + "' does not have " +
					      "any indexers defined");
				return null;
			}
			
			ix = new Indexers (mi);
			map [t] = ix;

			return ix;
		}
	}
	
	public class IndexerAccess : Expression, IAssignMethod {
		//
		// Points to our "data" repository
		//
		ElementAccess ea;
		MethodInfo get, set;
		Indexers ilist;
		ArrayList set_arguments;
		
		public IndexerAccess (ElementAccess ea_data)
		{
			ea = ea_data;
			eclass = ExprClass.Value;
		}

		public bool VerifyAssignable (Expression source)
		{
			throw new Exception ("Implement me!");
		}

		public override Expression DoResolve (EmitContext ec)
		{
			Type indexer_type = ea.Expr.Type;
			
			//
			// Step 1: Query for all `Item' *properties*.  Notice
			// that the actual methods are pointed from here.
			//
			// This is a group of properties, piles of them.  

			if (ilist == null)
				ilist = Indexers.GetIndexersForType (
					indexer_type, ec.TypeContainer.RootContext.TypeManager, ea.loc);
			
			if (ilist != null && ilist.getters != null && ilist.getters.Count > 0)
				get = (MethodInfo) Invocation.OverloadResolve (
					ec, new MethodGroupExpr (ilist.getters), ea.Arguments, ea.loc);

			if (get == null){
				Report.Error (154, ea.loc,
					      "indexer can not be used in this context, because " +
					      "it lacks a `get' accessor");
					return null;
			}
				
			type = get.ReturnType;
			eclass = ExprClass.Value;
			return this;
		}

		public override Expression DoResolveLValue (EmitContext ec, Expression right_side)
		{
			Type indexer_type = ea.Expr.Type;
			Type right_type = right_side.Type;

			if (ilist == null)
				ilist = Indexers.GetIndexersForType (
					indexer_type, ec.TypeContainer.RootContext.TypeManager, ea.loc);

			if (ilist != null && ilist.setters != null && ilist.setters.Count > 0){
				set_arguments = (ArrayList) ea.Arguments.Clone ();
				set_arguments.Add (new Argument (right_side, Argument.AType.Expression));

				set = (MethodInfo) Invocation.OverloadResolve (
					ec, new MethodGroupExpr (ilist.setters), set_arguments, ea.loc);
			}
			
			if (set == null){
				Report.Error (200, ea.loc,
					      "indexer X.this [" + TypeManager.CSharpName (right_type) +
					      "] lacks a `set' accessor");
					return null;
			}

			type = TypeManager.void_type;
			eclass = ExprClass.IndexerAccess;
			return this;
		}
		
		public override void Emit (EmitContext ec)
		{
			Invocation.EmitCall (ec, false, ea.Expr, get, ea.Arguments);
		}

		//
		// source is ignored, because we already have a copy of it from the
		// LValue resolution and we have already constructed a pre-cached
		// version of the arguments (ea.set_arguments);
		//
		public void EmitAssign (EmitContext ec, Expression source)
		{
			Invocation.EmitCall (ec, false, ea.Expr, set, set_arguments);
		}
	}
	
	public class BaseAccess : Expression {

		public enum BaseAccessType {
			Member,
			Indexer
		};
		
		public readonly BaseAccessType BAType;
		public readonly string         Member;
		public readonly ArrayList      Arguments;

		public BaseAccess (BaseAccessType t, string member, ArrayList args)
		{
			BAType = t;
			Member = member;
			Arguments = args;
			
		}

		public override Expression DoResolve (EmitContext ec)
		{
			// FIXME: Implement;
			throw new Exception ("Unimplemented");
			// return this;
		}

		public override void Emit (EmitContext ec)
		{
			throw new Exception ("Unimplemented");
		}
	}

	// <summary>
	//   This class exists solely to pass the Type around and to be a dummy
	//   that can be passed to the conversion functions (this is used by
	//   foreach implementation to typecast the object return value from
	//   get_Current into the proper type.  All code has been generated and
	//   we only care about the side effect conversions to be performed
	// </summary>
	
	public class EmptyExpression : Expression {
		public EmptyExpression ()
		{
			type = TypeManager.object_type;
			eclass = ExprClass.Value;
		}

		public override Expression DoResolve (EmitContext ec)
		{
			return this;
		}

		public override void Emit (EmitContext ec)
		{
			// nothing, as we only exist to not do anything.
		}
	}

	public class UserCast : Expression {
		MethodBase method;
		Expression source;
		
		public UserCast (MethodInfo method, Expression source)
		{
			this.method = method;
			this.source = source;
			type = method.ReturnType;
			eclass = ExprClass.Value;
		}

		public override Expression DoResolve (EmitContext ec)
		{
			//
			// We are born fully resolved
			//
			return this;
		}

		public override void Emit (EmitContext ec)
		{
			ILGenerator ig = ec.ig;

			source.Emit (ec);
			
			if (method is MethodInfo)
				ig.Emit (OpCodes.Call, (MethodInfo) method);
			else
				ig.Emit (OpCodes.Call, (ConstructorInfo) method);

		}

	}
}
