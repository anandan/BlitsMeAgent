﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using BlitsMe.Agent.Components;
using BlitsMe.Agent.Components.Chat;
using BlitsMe.Agent.Components.Notification;
using BlitsMe.Agent.Components.Person;
using BlitsMe.Agent.Components.Search;
using BlitsMe.Agent.UI.WPF.Engage;
using BlitsMe.Agent.UI.WPF.Roster;
using BlitsMe.Agent.UI.WPF.Search;
using log4net;

namespace BlitsMe.Agent.UI.WPF
{
    /// <summary>
    /// Interaction logic for Dashboard.xaml
    /// </summary>
    public partial class Dashboard : Window
    {
        private static readonly ILog Logger = LogManager.GetLogger(typeof(Dashboard));
        private readonly BlitsMeClientAppContext _appContext;

        // Observable mirror for engagements as engagement windows
        private readonly EngagementWindowList _engagementWindows;
        // Observable mirror for Persons as RosterElements
        private readonly RosterList _rosterList;
        private readonly CollectionViewSource _notificationView;
        // Dispatching collection for notifications (to be used by everything)
        internal DispatchingCollection<ObservableCollection<INotification>, INotification> NotificationList { get; set; }
        private AddPersonControl _addPersonControl;
        private SearchWindow _searchWindow;
        private Timer _searchCountDown;
        private readonly Object _searchLock = new Object();

        public Dashboard(BlitsMeClientAppContext appContext)
        {
            this.InitializeComponent();
            this._appContext = appContext;
            // Handle the notifications
            NotificationList = new DispatchingCollection<ObservableCollection<INotification>, INotification>(_appContext.NotificationManager.Notifications, Dispatcher);
            _notificationView = new CollectionViewSource { Source = NotificationList };
            _notificationView.Filter += NotificationFilter;
            Notifications.ItemsSource = _notificationView.View;
            _appContext.NotificationManager.Notifications.CollectionChanged += NotificationsOnCollectionChanged;
            // Setup the various data contexts and sources
            _rosterList = new RosterList(_appContext, Dispatcher);
            _rosterList.SetList(_appContext.RosterManager.ServicePersonList, "Username");
            Team.LostFocus += Team_LostFocus;
            Team.DataContext = _rosterList.RosterViewSource;
            // Setup the engagementWindow list as a mirror of the engagements
            _engagementWindows = new EngagementWindowList(_appContext, NotificationList, Dispatcher);
            try
            {
                _engagementWindows.SetList(_appContext.EngagementManager.Engagements, "SecondPartyUsername");
            }
            catch (Exception e)
            {
                Logger.Error("Failed to set the list : " + e.Message, e);
            }
            _appContext.EngagementManager.NewActivity += EngagementManagerOnNewActivity;
        }

        private void NotificationsOnCollectionChanged(object sender, NotifyCollectionChangedEventArgs notifyCollectionChangedEventArgs)
        {
            // Multithreaded handling, make sure only dispatcher updates UI objects
            if (Dispatcher.CheckAccess())
                _notificationView.View.Refresh();
            else
                Dispatcher.Invoke(new Action(() => _notificationView.View.Refresh()));
        }

        internal void EngagementManagerOnNewActivity(object sender, EngagementActivityArgs args)
        {
            if (Dispatcher.CheckAccess())
            {
                ShowEngagement(args.Engagement.SecondParty);
            }
            else
            {
                Dispatcher.Invoke(new Action(() => ShowEngagement(args.Engagement.SecondParty)));
            }
        }

        private void NotificationFilter(object sender, FilterEventArgs eventArgs)
        {
            INotification notification = eventArgs.Item as INotification;
            if (notification != null && (notification.From == null || notification.From.Equals("")))
            {
                eventArgs.Accepted = true;
            }
            else
            {
                eventArgs.Accepted = false;
            }
        }

        #region Windowing Stuff

        private void WindowStateChanged(object sender, EventArgs e)
        {
            HideIfMinimized(sender, e);
        }

        private void HideIfMinimized(object sender, EventArgs e)
        {
            if (WindowState.Minimized == this.WindowState)
            {
                this.Hide();
            }
        }

        private void HideIfClosing(object sender, CancelEventArgs e)
        {
            e.Cancel = true;
            this.Hide();
        }

        #endregion

        #region Roster Handling

        // When we click on another button, Team must lose focus so when its clicked on again, the select item event fires.
        private void Team_LostFocus(object sender, RoutedEventArgs e)
        {
            Team.SelectedItem = null;
        }

        private void TeamMemberSelected(object sender, RoutedEventArgs e)
        {
            ListBoxItem item = e.Source as ListBoxItem;
            RosterElement rosterElement = item.Content as RosterElement;
            ShowEngagement(rosterElement.Person);
        }

        private void ShowEngagement(Person person)
        {
            EngagementWindow egw = _engagementWindows.GetEngagementWindow(person);
            if (egw != null)
            {
                ActiveContent.Content = egw;
            }
            else
            {
                Logger.Error("Failed to find an engagement window for peron " + person);
            }
        }

        private void AddPersonClick(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_addPersonControl == null)
            {
                _addPersonControl = new AddPersonControl();
            }
            ActiveContent.Content = _addPersonControl;
        }

        private void Search_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            // This gets called on init (where appContext is null), but we don't want to search that anyway
            if (_appContext != null)
            {
                if (!SearchBox.Text.Equals("") && !SearchBox.Text.Equals("Search"))
                {
                    lock (_searchLock)
                    {
                        // If its already enabled, reset it
                        if (_searchCountDown.Enabled)
                        {
                            // Reset the timer
                            _searchCountDown.Stop();
                        }
                        _searchCountDown.Start();
                    }
                }
            }
        }

        private void SearchBox_GotFocus(object sender, System.Windows.RoutedEventArgs e)
        {
            if ("Search".Equals(SearchBox.Text))
            {
                SearchBox.Text = "";
            }
            if (_searchWindow == null)
            {
                _searchWindow = new SearchWindow(_appContext);
                _searchCountDown = new Timer(500) { AutoReset = false };
                _searchCountDown.Elapsed += ProcessSearch;
            }
            ActiveContent.Content = _searchWindow;
        }

        private void ProcessSearch(object sender, ElapsedEventArgs elapsedEventArgs)
        {
            lock (_searchLock)
            {
                try
                {
                    String searchQuery = "";
                    if (Dispatcher.CheckAccess())
                    {
                        searchQuery = SearchBox.Text;
                    }
                    else
                    {
                        Dispatcher.Invoke(new Action(delegate { searchQuery = SearchBox.Text; }));
                    }
                    if (!searchQuery.Equals("") && !searchQuery.Equals("Search"))
                    {
                        _appContext.SearchManager.Search(searchQuery);
                    }
                }
                catch (Exception e)
                {
                    Logger.Error("Failed to perform search : " + e.Message, e);
                }
            }
        }

        private void SearchBox_LostFocus(object sender, System.Windows.RoutedEventArgs e)
        {
            if ("".Equals(SearchBox.Text))
            {
                SearchBox.Text = "Search";
            }
        }

        #endregion
    }

}