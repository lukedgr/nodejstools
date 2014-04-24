﻿/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the Apache License, Version 2.0, please send an email to 
 * vspython@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.NodejsTools.Analysis.Values;
using Microsoft.NodejsTools.Parsing;
using Microsoft.NodejsTools.Analysis.Analyzer;


namespace Microsoft.NodejsTools.Analysis {
    /// <summary>
    /// Encapsulates a single piece of code which can be analyzed.  Currently this could be a top-level module, a class definition, 
    /// a function definition, or a comprehension scope (generator, dict, set, or list on 3.x).  AnalysisUnit holds onto both the 
    /// AST of the code which is to be analyzed along with the scope in which the object is declared.
    /// 
    /// Our dependency tracking scheme works by tracking analysis units - when we add a dependency it is the current
    /// AnalysisUnit which is dependent upon the variable.  If the value of a variable changes then all of the dependent
    /// AnalysisUnit's will be re-enqueued.  This proceeds until we reach a fixed point.
    /// </summary>
    public class AnalysisUnit : ISet<AnalysisUnit> {
        internal EnvironmentRecord _scope;
        private ModuleValue _declaringModule;
#if DEBUG
        private long _analysisTime;
        private long _analysisCount;
        private static Stopwatch _sw = new Stopwatch();

        static AnalysisUnit() {
            _sw.Start();
        }
#endif

        public static AnalysisUnit EvalUnit = new AnalysisUnit(null, null, null, true);

        internal AnalysisUnit(Statement ast, EnvironmentRecord scope)
            : this(ast, (ast != null ? ast.GlobalParent : null), scope, false) {
        }

        internal AnalysisUnit(Node ast, JsAst tree, EnvironmentRecord scope, bool forEval)
        {
            Ast = ast;
            Tree = tree;
            _scope = scope;
            ForEval = forEval;
        }

        /// <summary>
        /// True if this analysis unit is currently in the queue.
        /// </summary>
        public bool IsInQueue;

        /// <summary>
        /// True if this analysis unit is being used to evaluate the result of the analysis.  In this
        /// mode we don't track references or re-queue items.
        /// </summary>
        public readonly bool ForEval;

        internal virtual ModuleValue GetDeclaringModule() {
            if (_scope != null) {
                var moduleScope = _scope.EnumerateTowardsGlobal.OfType<ModuleScope>().FirstOrDefault();
                if (moduleScope != null) {
                    return moduleScope.Module;
                }
            }
            return null;
        }

        /// <summary>
        /// The global scope that the code associated with this analysis unit is declared within.
        /// </summary>
        internal ModuleValue DeclaringModule {
            get {
                if (_declaringModule == null) {
                    _declaringModule = GetDeclaringModule();
                }
                return _declaringModule;
            }
        }

        /// <summary>
        /// Looks up a sequence of types associated with the name using the
        /// normal JavaScript semantics.
        /// 
        /// This function is only safe to call during analysis. After analysis
        /// has completed, use a <see cref="ModuleAnalysis"/> instance.
        /// </summary>
        /// <param name="node">The node to associate with the lookup.</param>
        /// <param name="name">The full name of the value to find.</param>
        /// <returns>
        /// All values matching the provided name, or null if the name could not
        /// be resolved.
        /// 
        /// An empty sequence is returned if the name is found but currently has
        /// no values.
        /// </returns>
        /// <remarks>
        /// Calling this function will associate this unit with the requested
        /// variable. Future updates to the variable may result in the unit
        /// being reanalyzed.
        /// </remarks>
        public IAnalysisSet FindAnalysisValueByName(Node node, string name)
        {
            foreach (var scope in Scope.EnumerateTowardsGlobal) {
                var refs = scope.GetVariable(node, this, name, true);
                if (refs != null) {
                    var linkedVars = scope.GetLinkedVariablesNoCreate(name);
                    if (linkedVars != null) {
                        foreach (var linkedVar in linkedVars) {
                            linkedVar.AddReference(node, this);
                        }
                    }
                    return refs.Types;
                }
            }

          return AnalysisSet.Empty;
        }
        
      internal ProjectEntry ProjectEntry
        {
            get { return DeclaringModule.ProjectEntry; }
        }

        public JsAnalyzer Analyzer {
            get { return DeclaringModule.ProjectEntry.Analyzer; }
        }

        public AnalysisUnit CopyForEval() {
            return new AnalysisUnit(Ast, Tree, _scope, true);
        }

        public void Enqueue() {
            if (!ForEval && !IsInQueue) {
                Analyzer.Queue.Append(this);
                AnalysisLog.Enqueue(Analyzer.Queue, this);
                this.IsInQueue = true;
            }
        }



        /// <summary>
        /// The AST which will be analyzed when this node is analyzed
        /// </summary>
        public readonly Node Ast;

        public readonly JsAst Tree;

        internal void Analyze(DDG ddg, CancellationToken cancel) {
#if DEBUG
            long startTime = _sw.ElapsedMilliseconds;
            try {
                _analysisCount += 1;
#endif
                if (cancel.IsCancellationRequested) {
                    return;
                }
                AnalyzeWorker(ddg, cancel);
#if DEBUG
            } finally {
                long endTime = _sw.ElapsedMilliseconds;
                var thisTime = endTime - startTime;
                _analysisTime += thisTime;
                if (thisTime >= 500 || (_analysisTime / _analysisCount) > 500) {
                    Trace.TraceWarning("Analyzed: {0} {1} ({2} count, {3}ms total, {4}ms mean)", this, thisTime, _analysisCount, _analysisTime, (double)_analysisTime / _analysisCount);
                }
            }
#endif
        }

        internal virtual void AnalyzeWorker(DDG ddg, CancellationToken cancel) {
            DeclaringModule.Scope.ClearLinkedVariables();

            ddg.SetCurrentUnit(this);
            Ast.Walk(ddg);

            List<KeyValuePair<string, VariableDef>> toRemove = null;

            foreach (var variableInfo in DeclaringModule.Scope.Variables) {
                variableInfo.Value.ClearOldValues(ProjectEntry);
                if (variableInfo.Value._dependencies.Count == 0 &&
                    variableInfo.Value.TypesNoCopy.Count == 0) {
                    if (toRemove == null) {
                        toRemove = new List<KeyValuePair<string, VariableDef>>();
                    }
                    toRemove.Add(variableInfo);
                }
            }
            if (toRemove != null) {
                foreach (var nameValue in toRemove) {
                    DeclaringModule.Scope.RemoveVariable(nameValue.Key);

                    // if anyone read this value it could now be gone (e.g. user 
                    // deletes a class definition) so anyone dependent upon it
                    // needs to be updated.
                    nameValue.Value.EnqueueDependents();
                }
            }
        }

        /// <summary>
        /// The chain of scopes in which this analysis is defined.
        /// </summary>
        internal EnvironmentRecord Scope {
            get { return _scope; }
        }

        public override string ToString() {
            return String.Format(
                "<{3}: Name={0} ({1}), NodeType={2}>",
                FullName,
                GetHashCode(),
                Ast != null ? Ast.GetType().Name : "<unknown>",
                GetType().Name
            );
        }

        /// <summary>
        /// Returns the fully qualified name of the analysis unit's scope
        /// including all outer scopes.
        /// </summary>
        internal string FullName {
            get {
                if (Scope != null) {
                    return string.Join(".", Scope.EnumerateFromGlobal.Select(s => s.Name));
                } else {
                    return "<Unnamed unit>";
                }
            }
        }

        #region SelfSet

        bool ISet<AnalysisUnit>.Add(AnalysisUnit item) {
            throw new NotImplementedException();
        }

        void ISet<AnalysisUnit>.ExceptWith(IEnumerable<AnalysisUnit> other) {
            throw new NotImplementedException();
        }

        void ISet<AnalysisUnit>.IntersectWith(IEnumerable<AnalysisUnit> other) {
            throw new NotImplementedException();
        }

        bool ISet<AnalysisUnit>.IsProperSubsetOf(IEnumerable<AnalysisUnit> other) {
            throw new NotImplementedException();
        }

        bool ISet<AnalysisUnit>.IsProperSupersetOf(IEnumerable<AnalysisUnit> other) {
            throw new NotImplementedException();
        }

        bool ISet<AnalysisUnit>.IsSubsetOf(IEnumerable<AnalysisUnit> other) {
            throw new NotImplementedException();
        }

        bool ISet<AnalysisUnit>.IsSupersetOf(IEnumerable<AnalysisUnit> other) {
            throw new NotImplementedException();
        }

        bool ISet<AnalysisUnit>.Overlaps(IEnumerable<AnalysisUnit> other) {
            throw new NotImplementedException();
        }

        bool ISet<AnalysisUnit>.SetEquals(IEnumerable<AnalysisUnit> other) {
            var enumerator = other.GetEnumerator();
            if (enumerator.MoveNext()) {
                if (((ISet<AnalysisUnit>)this).Contains(enumerator.Current)) {
                    return !enumerator.MoveNext();
                }
            }
            return false;
        }

        void ISet<AnalysisUnit>.SymmetricExceptWith(IEnumerable<AnalysisUnit> other) {
            throw new NotImplementedException();
        }

        void ISet<AnalysisUnit>.UnionWith(IEnumerable<AnalysisUnit> other) {
            throw new NotImplementedException();
        }

        void ICollection<AnalysisUnit>.Add(AnalysisUnit item) {
            throw new InvalidOperationException();
        }

        void ICollection<AnalysisUnit>.Clear() {
            throw new InvalidOperationException();
        }

        bool ICollection<AnalysisUnit>.Contains(AnalysisUnit item) {
            return item == this;
        }

        void ICollection<AnalysisUnit>.CopyTo(AnalysisUnit[] array, int arrayIndex) {
            throw new InvalidOperationException();
        }

        int ICollection<AnalysisUnit>.Count {
            get { return 1; }
        }

        bool ICollection<AnalysisUnit>.IsReadOnly {
            get { return true; }
        }

        bool ICollection<AnalysisUnit>.Remove(AnalysisUnit item) {
            throw new InvalidOperationException();
        }

        IEnumerator<AnalysisUnit> IEnumerable<AnalysisUnit>.GetEnumerator() {
            return new SetOfOneEnumerator<AnalysisUnit>(this);
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() {
            yield return this;
        }

        #endregion
    }
}
