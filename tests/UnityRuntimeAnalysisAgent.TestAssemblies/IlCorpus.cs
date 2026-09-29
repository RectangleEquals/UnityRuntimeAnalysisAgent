// IL corpus: methods that together use every IL operand type, exception clauses, generic and array calls, closures and
// async state machines. Decoded by the agent and by dnlib, and the two are compared.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Zoo.Il
{
    public interface IShape
    {
        double Area();
    }

    public abstract class Shape : IShape
    {
        public abstract double Area();

        public virtual string Describe() => "shape";
    }

    public sealed class Square : Shape
    {
        public double side = 2;

        public override double Area() => side * side;

        public override string Describe() => "square " + base.Describe();
    }

    public class Circle : IShape
    {
        public double radius = 1;

        public double Area() => Math.PI * radius * radius;
    }

    public static class Corpus
    {
        public static int counter;
        public static string label = "item_label";

        public static int Constants(int a)
        {
            var small = a + 100;                 // ldc.i4.s
            var large = a + 100000;              // ldc.i4
            long wide = a + 5000000000L;         // ldc.i8
            float single = a + 1.5f;             // ldc.r4
            double real = a + 2.25;              // ldc.r8
            return small + large + (int)(wide % 7) + (int)single + (int)real;
        }

        public static string Strings(int a) => a > 0 ? "item_added" : "item_removed";

        public static int Branches(int a)
        {
            if (a > 3)                           // short branch
            {
                return 1;
            }

            var total = 0;
            for (var i = 0; i < a; i++)
            {
                total += i * 31 + (i ^ 7) - (i % 3) + (i << 2) + (i >> 1) + (i & 5) + (i | 9);
                total += i * 37 + (i ^ 11) - (i % 5) + (i << 3) + (i >> 2) + (i & 6) + (i | 10);
                total += i * 41 + (i ^ 13) - (i % 7) + (i << 4) + (i >> 3) + (i & 7) + (i | 11);
                total += i * 43 + (i ^ 17) - (i % 9) + (i << 5) + (i >> 4) + (i & 8) + (i | 12);
            }

            return total;                        // the loop back-edge is a long branch
        }

        public static string Switch(int a)
        {
            switch (a)
            {
                case 0: return "zero";
                case 1: return "one";
                case 2: return "two";
                case 3: return "three";
                case 4: return "four";
                default: return "many";
            }
        }

        public static object Types(object o)
        {
            var type = typeof(Square);           // ldtoken
            var boxed = (object)counter;         // box
            var array = new int[3];              // newarr
            var shape = o as IShape;             // isinst
            var square = (Square)o;              // castclass
            var point = default(Point);          // initobj
            return type.Name + boxed + array.Length + shape + square + point.X;
        }

        public static int Fields(Square square)
        {
            counter++;                           // ldsfld / stsfld
            square.side = square.side + 1;       // ldfld / stfld
            ref var side = ref square.side;      // ldflda
            return (int)side + counter;
        }

        public static int Locals(int a)
        {
            var b = a + 1;
            var c = b + 1;
            var d = c + 1;
            var e = d + 1;
            var f = e + 1;                       // ldloc.s / stloc.s beyond the first four
            return a + b + c + d + e + f;
        }

        public static int ManyLocals(int a)
        {
            var v0 = a + 0;
            var v1 = a + 1;
            var v2 = a + 2;
            var v3 = a + 3;
            var v4 = a + 4;
            var v5 = a + 5;
            var v6 = a + 6;
            var v7 = a + 7;
            var v8 = a + 8;
            var v9 = a + 9;
            var v10 = a + 10;
            var v11 = a + 11;
            var v12 = a + 12;
            var v13 = a + 13;
            var v14 = a + 14;
            var v15 = a + 15;
            var v16 = a + 16;
            var v17 = a + 17;
            var v18 = a + 18;
            var v19 = a + 19;
            var v20 = a + 20;
            var v21 = a + 21;
            var v22 = a + 22;
            var v23 = a + 23;
            var v24 = a + 24;
            var v25 = a + 25;
            var v26 = a + 26;
            var v27 = a + 27;
            var v28 = a + 28;
            var v29 = a + 29;
            var v30 = a + 30;
            var v31 = a + 31;
            var v32 = a + 32;
            var v33 = a + 33;
            var v34 = a + 34;
            var v35 = a + 35;
            var v36 = a + 36;
            var v37 = a + 37;
            var v38 = a + 38;
            var v39 = a + 39;
            var v40 = a + 40;
            var v41 = a + 41;
            var v42 = a + 42;
            var v43 = a + 43;
            var v44 = a + 44;
            var v45 = a + 45;
            var v46 = a + 46;
            var v47 = a + 47;
            var v48 = a + 48;
            var v49 = a + 49;
            var v50 = a + 50;
            var v51 = a + 51;
            var v52 = a + 52;
            var v53 = a + 53;
            var v54 = a + 54;
            var v55 = a + 55;
            var v56 = a + 56;
            var v57 = a + 57;
            var v58 = a + 58;
            var v59 = a + 59;
            var v60 = a + 60;
            var v61 = a + 61;
            var v62 = a + 62;
            var v63 = a + 63;
            var v64 = a + 64;
            var v65 = a + 65;
            var v66 = a + 66;
            var v67 = a + 67;
            var v68 = a + 68;
            var v69 = a + 69;
            var v70 = a + 70;
            var v71 = a + 71;
            var v72 = a + 72;
            var v73 = a + 73;
            var v74 = a + 74;
            var v75 = a + 75;
            var v76 = a + 76;
            var v77 = a + 77;
            var v78 = a + 78;
            var v79 = a + 79;
            var v80 = a + 80;
            var v81 = a + 81;
            var v82 = a + 82;
            var v83 = a + 83;
            var v84 = a + 84;
            var v85 = a + 85;
            var v86 = a + 86;
            var v87 = a + 87;
            var v88 = a + 88;
            var v89 = a + 89;
            var v90 = a + 90;
            var v91 = a + 91;
            var v92 = a + 92;
            var v93 = a + 93;
            var v94 = a + 94;
            var v95 = a + 95;
            var v96 = a + 96;
            var v97 = a + 97;
            var v98 = a + 98;
            var v99 = a + 99;
            var v100 = a + 100;
            var v101 = a + 101;
            var v102 = a + 102;
            var v103 = a + 103;
            var v104 = a + 104;
            var v105 = a + 105;
            var v106 = a + 106;
            var v107 = a + 107;
            var v108 = a + 108;
            var v109 = a + 109;
            var v110 = a + 110;
            var v111 = a + 111;
            var v112 = a + 112;
            var v113 = a + 113;
            var v114 = a + 114;
            var v115 = a + 115;
            var v116 = a + 116;
            var v117 = a + 117;
            var v118 = a + 118;
            var v119 = a + 119;
            var v120 = a + 120;
            var v121 = a + 121;
            var v122 = a + 122;
            var v123 = a + 123;
            var v124 = a + 124;
            var v125 = a + 125;
            var v126 = a + 126;
            var v127 = a + 127;
            var v128 = a + 128;
            var v129 = a + 129;
            var v130 = a + 130;
            var v131 = a + 131;
            var v132 = a + 132;
            var v133 = a + 133;
            var v134 = a + 134;
            var v135 = a + 135;
            var v136 = a + 136;
            var v137 = a + 137;
            var v138 = a + 138;
            var v139 = a + 139;
            var v140 = a + 140;
            var v141 = a + 141;
            var v142 = a + 142;
            var v143 = a + 143;
            var v144 = a + 144;
            var v145 = a + 145;
            var v146 = a + 146;
            var v147 = a + 147;
            var v148 = a + 148;
            var v149 = a + 149;
            var v150 = a + 150;
            var v151 = a + 151;
            var v152 = a + 152;
            var v153 = a + 153;
            var v154 = a + 154;
            var v155 = a + 155;
            var v156 = a + 156;
            var v157 = a + 157;
            var v158 = a + 158;
            var v159 = a + 159;
            var v160 = a + 160;
            var v161 = a + 161;
            var v162 = a + 162;
            var v163 = a + 163;
            var v164 = a + 164;
            var v165 = a + 165;
            var v166 = a + 166;
            var v167 = a + 167;
            var v168 = a + 168;
            var v169 = a + 169;
            var v170 = a + 170;
            var v171 = a + 171;
            var v172 = a + 172;
            var v173 = a + 173;
            var v174 = a + 174;
            var v175 = a + 175;
            var v176 = a + 176;
            var v177 = a + 177;
            var v178 = a + 178;
            var v179 = a + 179;
            var v180 = a + 180;
            var v181 = a + 181;
            var v182 = a + 182;
            var v183 = a + 183;
            var v184 = a + 184;
            var v185 = a + 185;
            var v186 = a + 186;
            var v187 = a + 187;
            var v188 = a + 188;
            var v189 = a + 189;
            var v190 = a + 190;
            var v191 = a + 191;
            var v192 = a + 192;
            var v193 = a + 193;
            var v194 = a + 194;
            var v195 = a + 195;
            var v196 = a + 196;
            var v197 = a + 197;
            var v198 = a + 198;
            var v199 = a + 199;
            var v200 = a + 200;
            var v201 = a + 201;
            var v202 = a + 202;
            var v203 = a + 203;
            var v204 = a + 204;
            var v205 = a + 205;
            var v206 = a + 206;
            var v207 = a + 207;
            var v208 = a + 208;
            var v209 = a + 209;
            var v210 = a + 210;
            var v211 = a + 211;
            var v212 = a + 212;
            var v213 = a + 213;
            var v214 = a + 214;
            var v215 = a + 215;
            var v216 = a + 216;
            var v217 = a + 217;
            var v218 = a + 218;
            var v219 = a + 219;
            var v220 = a + 220;
            var v221 = a + 221;
            var v222 = a + 222;
            var v223 = a + 223;
            var v224 = a + 224;
            var v225 = a + 225;
            var v226 = a + 226;
            var v227 = a + 227;
            var v228 = a + 228;
            var v229 = a + 229;
            var v230 = a + 230;
            var v231 = a + 231;
            var v232 = a + 232;
            var v233 = a + 233;
            var v234 = a + 234;
            var v235 = a + 235;
            var v236 = a + 236;
            var v237 = a + 237;
            var v238 = a + 238;
            var v239 = a + 239;
            var v240 = a + 240;
            var v241 = a + 241;
            var v242 = a + 242;
            var v243 = a + 243;
            var v244 = a + 244;
            var v245 = a + 245;
            var v246 = a + 246;
            var v247 = a + 247;
            var v248 = a + 248;
            var v249 = a + 249;
            var v250 = a + 250;
            var v251 = a + 251;
            var v252 = a + 252;
            var v253 = a + 253;
            var v254 = a + 254;
            var v255 = a + 255;
            var v256 = a + 256;
            var v257 = a + 257;
            var v258 = a + 258;
            var v259 = a + 259;
            return v0 + v1 + v2 + v3 + v4 + v5 + v6 + v7 + v8 + v9 + v10 + v11 + v12 + v13 + v14 + v15 + v16 + v17 + v18 + v19 + v20 + v21 + v22 + v23 + v24 + v25 + v26 + v27 + v28 + v29 + v30 + v31 + v32 + v33 + v34 + v35 + v36 + v37 + v38 + v39 + v40 + v41 + v42 + v43 + v44 + v45 + v46 + v47 + v48 + v49 + v50 + v51 + v52 + v53 + v54 + v55 + v56 + v57 + v58 + v59 + v60 + v61 + v62 + v63 + v64 + v65 + v66 + v67 + v68 + v69 + v70 + v71 + v72 + v73 + v74 + v75 + v76 + v77 + v78 + v79 + v80 + v81 + v82 + v83 + v84 + v85 + v86 + v87 + v88 + v89 + v90 + v91 + v92 + v93 + v94 + v95 + v96 + v97 + v98 + v99 + v100 + v101 + v102 + v103 + v104 + v105 + v106 + v107 + v108 + v109 + v110 + v111 + v112 + v113 + v114 + v115 + v116 + v117 + v118 + v119 + v120 + v121 + v122 + v123 + v124 + v125 + v126 + v127 + v128 + v129 + v130 + v131 + v132 + v133 + v134 + v135 + v136 + v137 + v138 + v139 + v140 + v141 + v142 + v143 + v144 + v145 + v146 + v147 + v148 + v149 + v150 + v151 + v152 + v153 + v154 + v155 + v156 + v157 + v158 + v159 + v160 + v161 + v162 + v163 + v164 + v165 + v166 + v167 + v168 + v169 + v170 + v171 + v172 + v173 + v174 + v175 + v176 + v177 + v178 + v179 + v180 + v181 + v182 + v183 + v184 + v185 + v186 + v187 + v188 + v189 + v190 + v191 + v192 + v193 + v194 + v195 + v196 + v197 + v198 + v199 + v200 + v201 + v202 + v203 + v204 + v205 + v206 + v207 + v208 + v209 + v210 + v211 + v212 + v213 + v214 + v215 + v216 + v217 + v218 + v219 + v220 + v221 + v222 + v223 + v224 + v225 + v226 + v227 + v228 + v229 + v230 + v231 + v232 + v233 + v234 + v235 + v236 + v237 + v238 + v239 + v240 + v241 + v242 + v243 + v244 + v245 + v246 + v247 + v248 + v249 + v250 + v251 + v252 + v253 + v254 + v255 + v256 + v257 + v258 + v259;
        }

        public static unsafe int FunctionPointer(int a)
        {
            delegate*<int, int> twice = &Twice;
            return twice(a);                     // calli (a signature operand)
        }

        public static int Twice(int a) => a * 2;

        public static int Exceptions(int a)
        {
            try
            {
                return 10 / a;
            }
            catch (DivideByZeroException) when (a == 0)
            {
                return -1;
            }
            catch (Exception)
            {
                return -2;
            }
            finally
            {
                counter--;
            }
        }

        public static int Calls(List<int> list, IShape shape, Shape baseShape)
        {
            list.Add(1);                                     // callvirt on a generic instantiation
            var box = new Box<string> { item = "x" };        // newobj of a generic instantiation
            var mapped = box.Map<int>(s => s.Length);        // generic method + closure-free lambda
            var grid = new int[2, 2];
            grid[1, 1] = 3;                                  // array Set (runtime-provided)
            Func<int> read = () => list.Count;               // closure
            Action act = Tick;                               // ldftn
            act();
            return (int)shape.Area() + (int)baseShape.Area() + mapped + grid[1, 1] + read();
        }

        public static void Tick() => counter++;

        public static async Task<int> LaterAsync(int a)
        {
            await Task.Yield();
            return Twice(a);
        }

        public static double Area(IShape shape) => shape.Area();

        public static string Describe(Shape shape) => shape.Describe();
    }
}
