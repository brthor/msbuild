﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.Build.Internal;
using Microsoft.Build.Shared;

namespace Microsoft.Build.Evaluation
{

    /// <summary>
    /// Represents the elements of an item specification string and 
    /// provides some operations over them (like matching items against a given ItemSpec)
    /// </summary>
    internal class ItemSpec<P, I>
        where P : class, IProperty
        where I : class, IItem
    {
        public string ItemSpecString { get; }

        /// <summary>
        /// The fragments that compose an item spec string (values, globs, item references)
        /// </summary>
        public IEnumerable<ItemFragment> Fragments { get; }

        /// <summary>
        /// The expander needs to have a default item factory set.
        /// </summary>
        public Expander<P, I> Expander { get; set; }

        /// <summary>
        /// The xml attribute where this itemspec comes from
        /// </summary>
        public IElementLocation ItemSpecLocation { get; }

        /// <param name="itemSpec">The string containing item syntax</param>
        /// <param name="expander">Expects the expander to have a default item factory set</param>
        /// <param name="itemSpecLocation">The xml location the itemspec comes from</param>
        /// <param name="expandProperties">Expand properties before breaking down fragments. Defaults to true</param>
        public ItemSpec(string itemSpec, Expander<P, I> expander, IElementLocation itemSpecLocation, bool expandProperties = true)
        {
            ItemSpecString = itemSpec;
            Expander = expander;
            ItemSpecLocation = itemSpecLocation;

            Fragments = BuildItemFragments(itemSpecLocation, expandProperties);
        }

        private IEnumerable<ItemFragment> BuildItemFragments(IElementLocation itemSpecLocation, bool expandProperties)
        {
            var builder = ImmutableList.CreateBuilder<ItemFragment>();

            //  Code corresponds to Evaluator.CreateItemsFromInclude
            var evaluatedItemspecEscaped = ItemSpecString;

            if (string.IsNullOrEmpty(evaluatedItemspecEscaped))
            {
                return builder.ToImmutable();
            }

            // STEP 1: Expand properties in Include
            if (expandProperties)
            {
                evaluatedItemspecEscaped = Expander.ExpandIntoStringLeaveEscaped(ItemSpecString, ExpanderOptions.ExpandProperties, itemSpecLocation);
            }

            // STEP 2: Split Include on any semicolons, and take each split in turn
            if (evaluatedItemspecEscaped.Length > 0)
            {
                var splitsEscaped = ExpressionShredder.SplitSemiColonSeparatedList(evaluatedItemspecEscaped);

                foreach (var splitEscaped in splitsEscaped)
                {
                    // STEP 3: If expression is "@(x)" copy specified list with its metadata, otherwise just treat as string
                    bool isItemListExpression;
                    var itemReferenceFragment = ProcessItemExpression(splitEscaped, itemSpecLocation, out isItemListExpression);

                    if (isItemListExpression)
                    {
                        builder.Add(itemReferenceFragment);
                    }
                    else
                    {
                        // The expression is not of the form "@(X)". Treat as string

                        //  Code corresponds to EngineFileUtilities.GetFileList
                        var containsEscapedWildcards = EscapingUtilities.ContainsEscapedWildcards(splitEscaped);
                        var containsRealWildcards = FileMatcher.HasWildcards(splitEscaped);

                        // '*' is an illegal character to have in a filename.
                        // todo file-system assumption on legal path characters: https://github.com/Microsoft/msbuild/issues/781
                        if (containsEscapedWildcards && containsRealWildcards)
                        {

                            // Just return the original string.
                            builder.Add(new ValueFragment(splitEscaped, itemSpecLocation.File));
                        }
                        else if (!containsEscapedWildcards && containsRealWildcards)
                        {
                            // Unescape before handing it to the filesystem.
                            var filespecUnescaped = EscapingUtilities.UnescapeAll(splitEscaped);

                            builder.Add(new GlobFragment(filespecUnescaped, itemSpecLocation.File));
                        }
                        else
                        {
                            // No real wildcards means we just return the original string.  Don't even bother 
                            // escaping ... it should already be escaped appropriately since it came directly
                            // from the project file

                            builder.Add(new ValueFragment(splitEscaped, itemSpecLocation.File));
                        }
                    }
                }
            }

            return builder.ToImmutable();
        }

        private ItemExpressionFragment<P, I> ProcessItemExpression(string expression, IElementLocation elementLocation, out bool isItemListExpression)
        {
            isItemListExpression = false;

            //  Code corresponds to Expander.ExpandSingleItemVectorExpressionIntoItems
            if (expression.Length == 0)
            {
                return null;
            }

            var capture = Expander<P, I>.ExpandSingleItemVectorExpressionIntoExpressionCapture(expression, ExpanderOptions.ExpandItems, elementLocation);

            if (capture == null)
            {
                return null;
            }

            isItemListExpression = true;

            return new ItemExpressionFragment<P, I>(capture, expression, this);
        }

        /// <summary>
        /// Return the items in <param name="items"/> that match this itemspec
        /// </summary>
        public IEnumerable<I> FilterItems(IEnumerable<I> items)
        {
            return items.Where(i => Fragments.Any(f => f.ItemMatches(i.EvaluatedInclude) > 0));
        }

        /// <summary>
        /// Return the fragments that match against the given <param name="itemToMatch"/>
        /// </summary>
        /// <param name="matches">
        /// Total number of matches. Some fragments match more than once (item expression may contain multiple instances of <param name="itemToMatch"/>)
        /// </param>
        public IEnumerable<ItemFragment> FragmentsMatchingItem(string itemToMatch, out int matches)
        {
            var result = new List<ItemFragment>(Fragments.Count());
            matches = 0;

            foreach (var fragment in Fragments)
            {
                var itemMatches = fragment.ItemMatches(itemToMatch);

                if (itemMatches > 0)
                {
                    result.Add(fragment);
                    matches += itemMatches;
                }
            }

            return result;
        }
    }

    internal abstract class ItemFragment
    {
        /// <summary>
        /// The substring from the original itemspec representing this fragment
        /// </summary>
        public string ItemSpecFragment { get; }

        /// <summary>
        /// Path of the projet the itemspec is coming from
        /// </summary>
        protected string ProjectPath { get; }

        /// <summary>
        /// Function that checks if a given string matches the <see cref="ItemSpecFragment"/>
        /// </summary>
        protected Lazy<Func<string, bool>> FileMatcher { get; }

        protected ItemFragment(string itemSpecFragment, string projectPath)
            : this(
                itemSpecFragment,
                projectPath,
                new Lazy<Func<string, bool>>(() => EngineFileUtilities.GetMatchTester(itemSpecFragment, projectPath)))
        {
        }

        protected ItemFragment(string itemSpecFragment, string projectPath, Lazy<Func<string, bool>> fileMatcher)
        {
            ItemSpecFragment = itemSpecFragment;
            ProjectPath = projectPath;
            FileMatcher = fileMatcher;
        }

        /// <returns>The number of times the <param name="itemToMatch"></param> appears in this fragment</returns>
        public virtual int ItemMatches(string itemToMatch)
        {
            return FileMatcher.Value(itemToMatch) ? 1 : 0;
        }
    }

    internal class ValueFragment : ItemFragment
    {
        public ValueFragment(string itemSpecFragment, string projectPath)
            : base(itemSpecFragment, projectPath)
        {
        }
    }

    internal class GlobFragment : ItemFragment
    {
        public GlobFragment(string itemSpecFragment, string projectPath)
            : base(itemSpecFragment, projectPath)
        {
        }
    }

    internal class ItemExpressionFragment<P, I> : ItemFragment
        where P : class, IProperty
        where I : class, IItem
    {
        public ExpressionShredder.ItemExpressionCapture Capture { get; }

        private readonly ItemSpec<P, I> _containingItemSpec;
        private IList<ValueFragment> _itemValueFragments;
        private Expander<P, I> _expander;

        public ItemExpressionFragment(ExpressionShredder.ItemExpressionCapture capture, string itemSpecFragment, ItemSpec<P, I> containingItemSpec) 
            : base(itemSpecFragment, containingItemSpec.ItemSpecLocation.File)
        {
            Capture = capture;

            _containingItemSpec = containingItemSpec;
            _expander = _containingItemSpec.Expander;
        }

        public override int ItemMatches(string itemToMatch)
        {
            // cache referenced items as long as the expander does not change
            // reference equality works for now since the expander cannot mutate its item state (hopefully it stays that way)
            if (_itemValueFragments == null || _expander != _containingItemSpec.Expander)
            {
                _expander = _containingItemSpec.Expander;

                IList<Tuple<string, I>> itemsFromCapture;
                bool throwaway;
                _expander.ExpandExpressionCapture(Capture, _containingItemSpec.ItemSpecLocation, ExpanderOptions.ExpandItems, false /* do not include null expansion results */, out throwaway, out itemsFromCapture);
                _itemValueFragments = itemsFromCapture.Select(i => new ValueFragment(i.Item1, ProjectPath)).ToList();
            }

            return _itemValueFragments.Count(v => v.ItemMatches(itemToMatch) > 0);
        }
    }
}