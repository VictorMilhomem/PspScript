Program        ::= TopDecl* EOF

TopDecl        ::= ImportDecl
                 | GameConfig
                 | AssetDecl
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
ConfigField    ::= IDENT ":" (INT | SIZE_LITERAL | STRING | IDENT) ","?
// E.g.: ram_budget: 16MB, fps: 60, entry: Match

// Assets processed at compile time
AssetDecl      ::= "asset" IDENT "=" IDENT "(" STRING ("," ArgList)? ")"
// E.g.: asset ship = texture("ship.png", format: RGBA4444)

// Messages (compile to a global Tagged Union)
MessageDecl    ::= "message" IDENT "{" FieldList? "}"

// Passive data structures (values)
StructDecl     ::= "struct" IDENT "{" FieldList? "}"

// Nominal enumerated types (compile to C99 typedef enum)
EnumDecl       ::= "enum" IDENT "{" (IDENT ("," IDENT)* ","?)? "}"

// Actors with static capacity defined on the type (pool) or singleton (no brackets)
ActorDecl      ::= "actor" IDENT ("[" INT "]")? "{" ActorMember* "}"
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
ArrayType      ::= "[" INT "]" Type   // Fixed-size arrays only (no dynamic heap)

GlobalVarDecl  ::= ("level" | "global")? "var" IDENT (":" Type)? ("=" Expr)?
FieldDecl      ::= IDENT ":" Type ("=" Expr)?
FieldList      ::= FieldDecl (("," | NEWLINE) FieldDecl)*

LocalVarDecl   ::= "temp"? "var" IDENT (":" Type)? ("=" Expr)?
// "temp var" allocates on FrameArena; normal "var" resides on the C stack

Block          ::= "{" Stmt* "}"

Stmt           ::= LocalVarDecl
                 | AssignStmt
                 | IfStmt
                 | MatchStmt
                 | ForStmt
                 | ForRangeStmt
                 | ForEachStmt
                 | BreakStmt
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
MatchArm       ::= MatchPattern "=>" (Block | Stmt) ","?
MatchPattern   ::= "_" | IDENT "." IDENT | "-"? INT
ForStmt        ::= "for" Expr Block
ForRangeStmt   ::= "for" IDENT "in" Expr ".." Expr Block
BreakStmt      ::= "break"
ReturnStmt     ::= "return" Expr?
AssignStmt     ::= Expr ("=" | "+=" | "-=" | "*=" | "/=" | "&=" | "|=" | "^=" | "<<=" | ">>=") Expr
ExprStmt       ::= Expr

Expr           ::= LogicOr
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