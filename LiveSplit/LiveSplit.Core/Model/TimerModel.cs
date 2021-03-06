﻿using LiveSplit.Model.Input;
using System;
using System.Linq;

namespace LiveSplit.Model
{
    public class TimerModel : ITimerModel
    {
        public LiveSplitState CurrentState
        {
            get
            {
                return _CurrentState;
            }
            set
            {
                _CurrentState = value;
                value?.RegisterTimerModel(this);
            }
        }

        private LiveSplitState _CurrentState;

        public event EventHandler OnSplit;
        public event EventHandler OnUndoSplit;
        public event EventHandler OnSkipSplit;
        public event EventHandler OnStart;
        public event EventHandlerT<TimerPhase> OnReset;
        public event EventHandler OnPause;
        public event EventHandler OnUndoAllPauses;
        public event EventHandler OnResume;
        public event EventHandler OnScrollUp;
        public event EventHandler OnScrollDown;
        public event EventHandler OnSwitchComparisonPrevious;
        public event EventHandler OnSwitchComparisonNext;

        public void Start()
        {
            if (CurrentState.CurrentPhase == TimerPhase.NotRunning)
            {
                CurrentState.CurrentPhase = TimerPhase.Running;
                CurrentState.CurrentSplitIndex = 0;
                CurrentState.AttemptStarted = TimeStamp.CurrentDateTime;
                CurrentState.AdjustedStartTime = CurrentState.StartTimeWithOffset = TimeStamp.Now - CurrentState.Run.Offset;
                CurrentState.StartTime = TimeStamp.Now;
                CurrentState.TimePausedAt = CurrentState.Run.Offset;
                CurrentState.IsGameTimeInitialized = false;
                if( CurrentState.Run.Count > 0 ) {
                    CurrentState.CurrentSplit.DeathCount = 0;
                    if( CurrentState.CurrentSplit.Parent != null ) {
                        CurrentState.CurrentSplit.Parent.DeathCount = 0;
                    }
                }

                CurrentState.Run.AttemptCount++;
                CurrentState.Run.HasChanged = true;

                OnStart?.Invoke(this,null);
            }
        }

        public void LoadFrozenRun()
        {
            var ongoingRun = CurrentState.Run.FrozenRun;
            if (CurrentState.CurrentPhase != TimerPhase.NotRunning || ongoingRun == null)
            {
                return;
            }

            var count = ongoingRun.SplitDeaths.Count;
            if( count == 0 )
            {
                return;
            }
            Time lastSplitTime = Time.Zero;
            for (var i = 0; i < count; i++)
            {
                CurrentState.Run[i].DeathCount = ongoingRun.SplitDeaths[i];
                lastSplitTime = ongoingRun.SplitEndTimes[i];
                CurrentState.Run[i].SplitTime = lastSplitTime;
            }

            CurrentState.Run[count - 1].SplitTime = default( Time );
            var endTime = lastSplitTime.RealTime.Value;
            var offsetEndTime = endTime - CurrentState.Run.Offset;
            var currentAtomicTime = TimeStamp.CurrentDateTime;
            var beginDateTime = currentAtomicTime.Time - offsetEndTime;
            var beginTimeStamp = TimeStamp.Now - offsetEndTime;

            CurrentState.CurrentSplitIndex = count - 1;
            CurrentState.CurrentPhase = TimerPhase.Running;
            CurrentState.AttemptStarted = new AtomicDateTime( beginDateTime, currentAtomicTime.SyncedWithAtomicClock );
            CurrentState.StartTime = beginTimeStamp;
            CurrentState.AdjustedStartTime = CurrentState.StartTimeWithOffset = beginTimeStamp - CurrentState.Run.Offset;
            CurrentState.TimePausedAt = CurrentState.Run.Offset;
            CurrentState.IsGameTimeInitialized = false;
            RecountLastParentDeaths();

            var totalDeaths = 0;
            foreach( var split in CurrentState.Run )
            {
                if( split.Parent == null && split.DeathCount > 0 )
                {
                    totalDeaths += split.DeathCount;
                }
            }
            CurrentState.Run.CurrentDeathCount = totalDeaths;

            OnStart?.Invoke(this, null);
            Pause();
        }

        private void RecountLastParentDeaths()
        {
            var parent = CurrentState.CurrentSplit.Parent;
            if ( parent != null )
            {
                parent.DeathCount = CurrentState.CurrentSplit.DeathCount;
                for( var i = CurrentState.CurrentSplitIndex - 1; i >= 0; i-- )
                {
                    var split = CurrentState.Run[i];
                    if( IsSameParent( split, CurrentState.CurrentSplit ) )
                    {
                        parent.DeathCount += split.DeathCount;
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }

        public void InitializeGameTime() => CurrentState.IsGameTimeInitialized = true;

        private bool DoAdvanceSplit()
        {
            CurrentState.CurrentSplit.SplitTime = CurrentState.CurrentTime;
            CurrentState.CurrentSplitIndex++;
            if (CurrentState.Run.Count == CurrentState.CurrentSplitIndex) {
                CurrentState.CurrentPhase = TimerPhase.Ended;
                CurrentState.AttemptEnded = TimeStamp.CurrentDateTime;

                return false;
            } 
            return true;
        }

        private bool IsParentOf( ISegment target, ISegment child )
        {
            return object.ReferenceEquals( target, child.Parent );
        }

        public void Split()
        {
            if (CurrentState.CurrentPhase == TimerPhase.Running && CurrentState.CurrentTime.RealTime > TimeSpan.Zero)
            {
                var prevSplit = CurrentState.CurrentSplit;
                if( DoAdvanceSplit() ) {
                    if( IsParentOf( CurrentState.CurrentSplit, prevSplit ) ) {
                        // Skip the parent split.
                        DoAdvanceSplit();
                    }
                    if( CurrentState.Run.Count != CurrentState.CurrentSplitIndex ) {
                        CurrentState.CurrentSplit.DeathCount = 0;
                        if( CurrentState.CurrentSplit.Parent != null && !IsSameParent(CurrentState.CurrentSplit, prevSplit) ) {
                            CurrentState.CurrentSplit.Parent.DeathCount = 0;
                        }
                    }
                }

                CurrentState.Run.HasChanged = true;
                OnSplit?.Invoke(this, null);
            }
        }

        private bool IsSameParent( ISegment left, ISegment right )
        {
            return object.ReferenceEquals( left.Parent, right.Parent );
        }

        public void DoSkipSplit()
        {
            CurrentState.CurrentSplit.SplitTime = default(Time);
            CurrentState.CurrentSplitIndex++;
        }

        private bool CheckAllSkipped()
        {
            var currentParent = CurrentState.CurrentSplit;
            for( int i = CurrentState.CurrentSplitIndex - 1; i >= 0; i-- ) {
                if( object.ReferenceEquals( CurrentState.Run[i].Parent, currentParent ) ) {
                    if( CurrentState.Run[i].DeathCount != -1 ) {
                        return false;
                    }
                } else {
                    break;
                }
            }
            return true;
        }

        public void SkipSplit()
        {
            if ((CurrentState.CurrentPhase == TimerPhase.Running
                || CurrentState.CurrentPhase == TimerPhase.Paused)
                && CurrentState.CurrentSplitIndex < CurrentState.Run.Count - 1)
            {
                var prevSplit = CurrentState.CurrentSplit;
                var newCurrentSplit = CurrentState.Run[CurrentState.CurrentSplitIndex + 1];
                var isNextParent = IsParentOf( newCurrentSplit, prevSplit);
                if( !isNextParent || CurrentState.CurrentSplitIndex < CurrentState.Run.Count - 2 ) {
                    CurrentState.CurrentSplit.DeathCount = -1;
                    DoSkipSplit();
                    if ( isNextParent ) {
                        if( CheckAllSkipped() ) {
                            CurrentState.CurrentSplit.DeathCount = -1;
                        }
                        DoSkipSplit();
                    }
                    if (CurrentState.Run.Count != CurrentState.CurrentSplitIndex) {
                        CurrentState.CurrentSplit.DeathCount = 0;
                        if ( CurrentState.CurrentSplit.Parent != null && !object.ReferenceEquals(CurrentState.CurrentSplit.Parent, prevSplit.Parent)) {
                            CurrentState.CurrentSplit.Parent.DeathCount = 0;
                        }
                    }

                    CurrentState.Run.HasChanged = true;
                    OnSkipSplit?.Invoke(this, null);
                }
            }
        }

        private void DoUndoSplit( int undoneSplitDeaths )
        {
            CurrentState.CurrentSplitIndex--;
            CurrentState.CurrentSplit.SplitTime = default(Time);

            if (CurrentState.CurrentSplit.DeathCount == -1) {
                CurrentState.CurrentSplit.DeathCount = 0;
            }
            CurrentState.CurrentSplit.DeathCount += undoneSplitDeaths;
        }

        public void UndoSplit()
        {
            if (CurrentState.CurrentPhase != TimerPhase.NotRunning
                && CurrentState.CurrentSplitIndex > 0)
            {

                ISegment prevParent;
                int undoneSplitDeaths;
                if ( CurrentState.CurrentPhase == TimerPhase.Ended ) { 
                    CurrentState.CurrentPhase = TimerPhase.Running;
                    undoneSplitDeaths = 0;
                    prevParent = null;
                } else {
                    undoneSplitDeaths = Math.Max( CurrentState.CurrentSplit.DeathCount, 0 );
                    CurrentState.CurrentSplit.DeathCount = -1;
                    prevParent = CurrentState.CurrentSplit.Parent;
                }
                DoUndoSplit( undoneSplitDeaths );

                if (prevParent != null && prevParent != CurrentState.CurrentSplit.Parent) {
                    prevParent.DeathCount = -1;
                }

                if ( CurrentState.CurrentSplitIndex > 0 && IsParentOf( CurrentState.CurrentSplit, CurrentState.Run[CurrentState.CurrentSplitIndex - 1] ) ) {
                    DoUndoSplit( undoneSplitDeaths );
                }

                CurrentState.Run.HasChanged = true;
                OnUndoSplit?.Invoke(this, null);
            }
        }

        public void AddDeaths( int addCount )
        {
            if( CurrentState.CurrentPhase == TimerPhase.Running ) {
                CurrentState.CurrentSplit.DeathCount += addCount;
                if( CurrentState.CurrentSplit.Parent != null ) {
                    CurrentState.CurrentSplit.Parent.DeathCount += addCount;
                }
                CurrentState.Run.CurrentDeathCount += addCount;
            }
        }

        public void Reset()
        {
            Reset(true);
        }

        public void Reset(bool updateSplits)
        {
            if (CurrentState.CurrentPhase != TimerPhase.NotRunning)
            {
                if (CurrentState.CurrentPhase != TimerPhase.Ended)
                    CurrentState.AttemptEnded = TimeStamp.CurrentDateTime;
                CurrentState.IsGameTimePaused = false;
                CurrentState.LoadingTimes = TimeSpan.Zero;

                if (updateSplits)
                {
                    if( CurrentState.CurrentSplitIndex >= 0 && CurrentState.CurrentSplitIndex < CurrentState.Run.Count ) {
                        CurrentState.CurrentSplit.DeathCount = -1;
                        if( CurrentState.CurrentSplit.Parent != null ) {
                            CurrentState.CurrentSplit.Parent.DeathCount = -1;
                        }
                    }

                    UpdateAttemptHistory();
                    UpdateBestSegments();
                    UpdatePBSplits();
                    UpdateSegmentHistory();
                }

                ResetSplits();

                CurrentState.Run.FixSplits();
            }
        }

        private void ResetSplits()
        {
            var oldPhase = CurrentState.CurrentPhase;
            CurrentState.CurrentPhase = TimerPhase.NotRunning;
            CurrentState.CurrentSplitIndex = -1;
            CurrentState.Run.CurrentDeathCount = 0;

            //Reset Splits
            foreach (var split in CurrentState.Run)
            {
                split.SplitTime = default(Time);
                split.DeathCount = -1;
            }

            OnReset?.Invoke(this, oldPhase);
        }

        public void Pause()
        {
            if (CurrentState.CurrentPhase == TimerPhase.Running)
            {
                CurrentState.TimePausedAt = CurrentState.CurrentTime.RealTime.Value;
                CurrentState.CurrentPhase = TimerPhase.Paused;
                OnPause?.Invoke(this, null);
            }
            else if (CurrentState.CurrentPhase == TimerPhase.Paused)
            {
                CurrentState.AdjustedStartTime = TimeStamp.Now - CurrentState.TimePausedAt;
                CurrentState.CurrentPhase = TimerPhase.Running;
                CurrentState.Run.HasChanged = true;
                OnResume?.Invoke(this, null);
            }
            else if (CurrentState.CurrentPhase == TimerPhase.NotRunning)
                 Start(); //fuck abahbob                
        }

        public void UndoAllPauses()
        {
            if (CurrentState.CurrentPhase == TimerPhase.Paused)
                Pause();

            var pauseTime = CurrentState.PauseTime ?? TimeSpan.Zero;
            if (CurrentState.CurrentPhase == TimerPhase.Ended)
                CurrentState.Run.Last().SplitTime += new Time(pauseTime, pauseTime);

            CurrentState.AdjustedStartTime = CurrentState.StartTimeWithOffset;    
            OnUndoAllPauses?.Invoke(this, null);
        }

        public void SwitchComparisonNext()
        {
            var comparisons = CurrentState.Run.Comparisons.ToList();
            CurrentState.CurrentComparison = 
                comparisons.ElementAt((comparisons.IndexOf(CurrentState.CurrentComparison) + 1) 
                % (comparisons.Count));
            OnSwitchComparisonNext?.Invoke(this, null);
        }

        public void SwitchComparisonPrevious()
        {
            var comparisons = CurrentState.Run.Comparisons.ToList();
            CurrentState.CurrentComparison = 
                comparisons.ElementAt((comparisons.IndexOf(CurrentState.CurrentComparison) - 1 + comparisons.Count())
                % (comparisons.Count));
            OnSwitchComparisonPrevious?.Invoke(this, null);
        }

        public void ScrollUp()
        {
            OnScrollUp?.Invoke(this, null);
        }

        public void ScrollDown()
        {
            OnScrollDown?.Invoke(this, null);
        }

        public void UpdateAttemptHistory()
        {
            Time time = new Time();
            if (CurrentState.CurrentPhase == TimerPhase.Ended)
                time = CurrentState.CurrentTime;
            var maxIndex = CurrentState.Run.AttemptHistory.DefaultIfEmpty().Max(x => x.Index);
            var newIndex = Math.Max(0, maxIndex + 1);
            var newAttempt = new Attempt(newIndex, time, CurrentState.AttemptStarted, CurrentState.AttemptEnded, CurrentState.PauseTime);
            CurrentState.Run.AttemptHistory.Add(newAttempt);
        }

        public void UpdateBestSegments()
        {
            if (CurrentState.CurrentPhase == TimerPhase.Ended && ( CurrentState.Run.BestDeathCount > CurrentState.Run.CurrentDeathCount || CurrentState.Run.BestDeathCount == -1 ))
            {
                CurrentState.Run.BestDeathCount = CurrentState.Run.CurrentDeathCount;
            }

            ISegment currentParent = null;
            TimeSpan? parentStartTimeRTA = TimeSpan.Zero;
            TimeSpan? parentStartGameTime = TimeSpan.Zero;

            TimeSpan? currentSegmentRTA = TimeSpan.Zero;
            TimeSpan? previousSplitTimeRTA = TimeSpan.Zero;
            TimeSpan? currentSegmentGameTime = TimeSpan.Zero;
            TimeSpan? previousSplitTimeGameTime = TimeSpan.Zero;
            foreach (var split in CurrentState.Run)
            {
                var newBestSegment = new Time(split.BestSegmentTime);

                if (currentParent != split.Parent && currentParent == null) {
                    parentStartTimeRTA = previousSplitTimeRTA;
                    parentStartGameTime = previousSplitTimeGameTime;
                }

                if (split.SplitTime.RealTime != null)
                {
                    if( currentParent == split ) {
                        currentSegmentRTA = split.SplitTime.RealTime - parentStartTimeRTA;
                    } else {
                        currentSegmentRTA = split.SplitTime.RealTime - previousSplitTimeRTA;
                    }

                    previousSplitTimeRTA = split.SplitTime.RealTime;
                    if (split.BestSegmentTime.RealTime == null || currentSegmentRTA < split.BestSegmentTime.RealTime)
                        newBestSegment.RealTime = currentSegmentRTA;
                }
                if (split.SplitTime.GameTime != null)
                {
                    if (currentParent == split) {
                        currentSegmentGameTime = split.SplitTime.GameTime - parentStartGameTime;
                    } else {
                        currentSegmentGameTime = split.SplitTime.GameTime - previousSplitTimeGameTime;
                    }

                    previousSplitTimeGameTime = split.SplitTime.GameTime;
                    if (split.BestSegmentTime.GameTime == null || currentSegmentGameTime < split.BestSegmentTime.GameTime)
                        newBestSegment.GameTime = currentSegmentGameTime;
                }
                currentParent = split.Parent;
                split.BestSegmentTime = newBestSegment;

                if( split.DeathCount >= 0 && ( split.DeathCount < split.BestDeathCount || split.BestDeathCount == -1 ) ) {
                    split.BestDeathCount = split.DeathCount;
                }
            }
        }

        public void UpdatePBSplits()
        {
            var curMethod = CurrentState.CurrentTimingMethod;
            if ((CurrentState.Run.Last().SplitTime[curMethod] != null && CurrentState.Run.Last().PersonalBestSplitTime[curMethod] == null) || CurrentState.Run.Last().SplitTime[curMethod] < CurrentState.Run.Last().PersonalBestSplitTime[curMethod])
                SetRunAsPB();
        }

        public void UpdateSegmentHistory()
        {
            TimeSpan? splitTimeRTA = TimeSpan.Zero;
            TimeSpan? splitTimeGameTime = TimeSpan.Zero;
            foreach (var split in CurrentState.Run.Take(CurrentState.CurrentSplitIndex))
            {
                var newTime = new Time();
                newTime.RealTime = split.SplitTime.RealTime - splitTimeRTA;
                newTime.GameTime = split.SplitTime.GameTime - splitTimeGameTime;
                split.SegmentHistory.Add(CurrentState.Run.AttemptHistory.Last().Index, newTime);
                if (split.SplitTime.RealTime.HasValue)
                    splitTimeRTA = split.SplitTime.RealTime;
                if (split.SplitTime.GameTime.HasValue)
                    splitTimeGameTime = split.SplitTime.GameTime;
            }
        }

        public void SetRunAsPB()
        {
            CurrentState.Run.ImportSegmentHistory();
            CurrentState.Run.FixSplits();
            foreach( var current in CurrentState.Run ) { 
                current.PersonalBestSplitTime = current.SplitTime;
                current.PersonalBestDeathCount = current.DeathCount;
            }
            CurrentState.Run.Metadata.RunID = null;
        }
    }
}
