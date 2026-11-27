Program        ::= TopDecl* EOF

TopDecl        ::= ImportDecl
                 | GameConfig
                 | AssetDecl
                 | ConstDecl
                 | MessageDecl
                 | StructDecl
                 | EnumDecl
                 | ActorDecl
                 | StateDecl
                 | GlobalVarDecl
                 | FuncDecl

// Compile-time module import (relative path or bare StdLib module name, auto-deduplicated)
ImportDecl     ::= "import" (STRING | IDENT)
// E.g.: import "actors/player.psps", import "collision", import collision

// Global EBOOT configuration and memory budget
GameConfig     ::= "game" STRING "{" ConfigField* "}"
ConfigField    ::= IDENT ":" (INT | SIZE_LITERAL | STRING | BOOL | IDENT) ","?
// E.g.: id: "PSPS00001", version: "01.00", ram_budget: 16MB, extra_ram: true, fps: 60, entry: Match, icon: "icon.png"

// Assets processed at compile time
AssetDecl      ::= "asset" IDENT "=" IDENT "(" STRING ("," ArgList)? ")"
// E.g.: asset ship = texture("ship.png", format: RGBA4444)

// Compile-time constants (zero RAM; usable in expressions, [N]T, actor pools, and match patterns)
ConstDecl      ::= "const" IDENT (":" Type)? "=" ConstExpr
// E.g.: const MAX_ENEMIES = 32, const GRAVITY: f32 = 420.0

// Messages (compile to a global Tagged Union)
MessageDecl    ::= "message" IDENT "{" FieldList? "}"

// Passive data structures (values)
StructDecl     ::= "struct" IDENT "{" FieldList? "}"

// Nominal enumerated types (compile to C99 typedef enum)
EnumDecl       ::= "enum" IDENT "{" (IDENT ("," IDENT)* ","?)? "}"

// Actors with static capacity defined on the type (pool) or singleton (no brackets)
ActorDecl      ::= "actor" IDENT ("[" ConstExpr "]")? "{" ActorMember* "}"
ActorMember    ::= FieldDecl
                 | LifecycleHook
                 | MessageHandler
                 | FuncDecl

LifecycleHook  ::= ("init" | "update" | "draw" | "exit") "(" ParamList? ")" Block
MessageHandler ::= "on" IDENT "(" IDENT ")" Block
// E.g.: on Hit(msg) { ... }

// Scenes / Game States (manage LevelArena reset)
StateDecl      ::= "state" IDENT "{" StateMember* "}"
StateMember    ::= FieldDecl | LifecycleHook | MessageHandler | FuncDecl

Type           ::= PrimitiveType
                 | GameType
                 | ActorRefType
                 | ArrayType
                 | IDENT              // User-defined struct

PrimitiveType  ::= "int" | "u8" | "u16" | "u32" | "f32" | "bool" | "void"
GameType       ::= "vec2" | "vec3" | "rect" | "color" | "timer"
ActorRefType   ::= "ref" "<" IDENT ">" // 32-bit generational handle
ArrayType      ::= "[" ConstExpr "]" Type   // Fixed-size arrays only (no dynamic heap)

GlobalVarDecl  ::= ("level" | "global")? "var" IDENT (":" Type)? ("=" Expr)?
FieldDecl      ::= IDENT (":" Type ("=" Expr)? | "=" Expr)
FieldList      ::= FieldDecl (("," | NEWLINE) FieldDecl)*

LocalVarDecl   ::= "temp"? "var" IDENT (":" Type)? ("=" Expr)?
LocalConstDecl ::= "const" IDENT (":" Type)? "=" ConstExpr
// "temp var" allocates on FrameArena; normal "var" resides on the C stack; "const" is folded at compile time

Block          ::= "{" Stmt* "}"

Stmt           ::= LocalVarDecl
                 | LocalConstDecl
                 | AssignStmt
                 | IfStmt
                 | MatchStmt
                 | ForStmt
                 | ForRangeStmt
                 | ForEachStmt
                 | BreakStmt
                 | ContinueStmt
                 | ReturnStmt
                 | SendStmt
                 | SpawnStmt
                 | DestroyStmt
                 | TransitionStmt
                 | ExprStmt

// Message passing and actor lifecycle
SendStmt       ::= "send" "(" Expr "," IDENT ("{" NamedArgList? "}")? ")"
// E.g.: send(target, Hit { amount: 10, knockback: { -50.0, 0.0 } })

SpawnStmt      ::= ("var" IDENT "=")? "spawn" IDENT ("{" NamedArgList? "}")?
// E.g.: var e = spawn Enemy { pos: { 100.0, 20.0 } }

DestroyStmt    ::= "destroy" Expr
// E.g.: destroy self

TransitionStmt ::= "goto" IDENT
// E.g.: goto GameOver (triggers LevelArena reset)

// Safe iteration over active actor pools
ForEachStmt    ::= "each" IDENT "in" IDENT Block
// E.g.: each b in Bullet { ... }

IfStmt         ::= "if" Expr Block ("else" (IfStmt | Block))?
MatchStmt      ::= "match" Expr "{" MatchArm* "}"
MatchArm       ::= MatchPattern ("|" MatchPattern)* "=>" (Block | Stmt) ","?
MatchExpr      ::= "match" Expr "{" MatchExprArm* "}"
MatchExprArm   ::= MatchPattern ("|" MatchPattern)* "=>" Expr ","?
MatchPattern   ::= "_" | IDENT "." IDENT | "-"? INT | IDENT
ForStmt        ::= "for" Expr Block
ForRangeStmt   ::= "for" IDENT "in" Expr (".." | "..=") Expr Block
BreakStmt      ::= "break"
ContinueStmt   ::= "continue"
ReturnStmt     ::= "return" Expr?
AssignStmt     ::= Expr ("=" | "+=" | "-=" | "*=" | "/=" | "&=" | "|=" | "^=" | "<<=" | ">>=") Expr
ExprStmt       ::= Expr

Expr           ::= Ternary
Ternary        ::= LogicOr ("?" Expr ":" Ternary)?
LogicOr        ::= LogicAnd ("||" LogicAnd)*
LogicAnd       ::= BitOr ("&&" BitOr)*
BitOr          ::= BitXor ("|" BitXor)*
BitXor         ::= BitAnd ("^" BitAnd)*
BitAnd         ::= Equality ("&" Equality)*
Equality       ::= Comparison (("==" | "!=") Comparison)*
Comparison     ::= Shift (("<" | "<=" | ">" | ">=") Shift)*
Shift          ::= Term (("<<" | ">>") Term)*
Term           ::= Factor (("+" | "-") Factor)*
Factor         ::= Unary (("*" | "/" | "%") Unary)*
Unary          ::= ("!" | "-" | "~") Unary | Postfix

Postfix        ::= Primary ( CallSuffix | FieldSuffix | IndexSuffix )*
CallSuffix     ::= "(" ArgList? ")"
FieldSuffix    ::= "." IDENT
IndexSuffix    ::= "[" Expr "]"

Primary        ::= INT
                 | FLOAT              // Automatically emits 'f' suffix in C
                 | TIME_LITERAL       // E.g.: 1.5s -> 1.5f, 500ms -> 0.5f
                 | COLOR_LITERAL      // E.g.: #FF00AA -> 0xFFAA00FF (ABGR)
                 | STRING
                 | "true" | "false" | "null" | "self"
                 | VecLiteral         // E.g.: { 10.0, 20.0 }
                 | IDENT
                 | "(" Expr ")"

VecLiteral     ::= "{" Expr "," Expr ("," Expr ("," Expr)?)? "}"