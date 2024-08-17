﻿using Xunit.Abstractions;

namespace Terminal.Gui.ViewTests;

public class VisibleTests (ITestOutputHelper _output) : TestsAllViews
{
    [Fact]
    public void Visible_False_Leaves ()
    {
        var view = new View
        {
            Id = "view",
            CanFocus = true
        };

        view.SetFocus ();
        Assert.True (view.HasFocus);

        view.Visible = false;
        Assert.False (view.HasFocus);
    }

    [Fact]
    public void Visible_False_Leaves_Subview ()
    {
        var view = new View
        {
            Id = "view",
            CanFocus = true
        };

        var subView = new View
        {
            Id = "subView",
            CanFocus = true
        };

        view.Add (subView);

        view.SetFocus ();
        Assert.True (view.HasFocus);
        Assert.True (subView.HasFocus);
        Assert.Equal (subView, view.GetFocused ());

        view.Visible = false;
        Assert.False (view.HasFocus);
        Assert.False (subView.HasFocus);
    }

    [Fact]
    public void Visible_False_Leaves_Subview2 ()
    {
        var view = new Window
        {
            Id = "view",
            CanFocus = true
        };

        var subView = new View
        {
            Id = "subView",
            CanFocus = true
        };

        view.Add (subView);

        view.SetFocus ();
        Assert.True (view.HasFocus);
        Assert.True (subView.HasFocus);
        Assert.Equal (subView, view.GetFocused ());

        view.Visible = false;
        Assert.False (view.HasFocus);
        Assert.False (subView.HasFocus);
    }

    [Fact]
    public void Visible_False_On_Subview_Leaves_Just_Subview ()
    {
        var view = new View
        {
            Id = "view",
            CanFocus = true
        };

        var subView = new View
        {
            Id = "subView",
            CanFocus = true
        };

        view.Add (subView);

        view.SetFocus ();
        Assert.True (view.HasFocus);
        Assert.True (subView.HasFocus);
        Assert.Equal (subView, view.GetFocused ());

        subView.Visible = false;
        Assert.True (view.HasFocus);
        Assert.False (subView.HasFocus);
    }

    [Fact]
    public void Visible_False_Focuses_Deepest_Focusable_Subview ()
    {
        var view = new View
        {
            Id = "view",
            CanFocus = true
        };

        var subView = new View
        {
            Id = "subView",
            CanFocus = true
        };

        var subViewSubView1 = new View
        {
            Id = "subViewSubView1",
            CanFocus = false
        };

        var subViewSubView2 = new View
        {
            Id = "subViewSubView2",
            CanFocus = true
        };

        var subViewSubView3 = new View
        {
            Id = "subViewSubView3",
            CanFocus = true // This is the one that will be focused
        };
        subView.Add (subViewSubView1, subViewSubView2, subViewSubView3);

        view.Add (subView);

        view.SetFocus ();
        Assert.True (subView.HasFocus);
        Assert.Equal (subView, view.GetFocused ());
        Assert.Equal (subViewSubView2, subView.GetFocused ());

        subViewSubView2.Visible = false;
        Assert.True (subView.HasFocus);
        Assert.Equal (subView, view.GetFocused ());
        Assert.Equal (subViewSubView3, subView.GetFocused ());
        Assert.True (subViewSubView3.HasFocus);
    }

    [Fact]
    public void Visible_True_Subview_Focuses_SubView ()
    {
        var view = new View
        {
            Id = "view",
            CanFocus = true,
            Visible = false
        };

        var subView = new View
        {
            Id = "subView",
            CanFocus = true
        };

        view.Add (subView);

        view.SetFocus ();
        Assert.False (view.HasFocus);
        Assert.False (subView.HasFocus);

        view.Visible = true;
        Assert.True (view.HasFocus);
        Assert.True (subView.HasFocus);
    }

    [Fact]
    public void Visible_True_On_Subview_Focuses ()
    {
        var view = new View
        {
            Id = "view",
            CanFocus = true
        };

        var subView = new View
        {
            Id = "subView",
            CanFocus = true,
            Visible = false
        };

        view.Add (subView);

        view.SetFocus ();
        Assert.True (view.HasFocus);
        Assert.False (subView.HasFocus);

        subView.Visible = true;
        Assert.True (view.HasFocus);
        Assert.True (subView.HasFocus);
    }

    [Fact]
    public void Visible_True_Focuses_Deepest_Focusable_Subview ()
    {
        var view = new View
        {
            Id = "view",
            CanFocus = true
        };

        var subView = new View
        {
            Id = "subView",
            CanFocus = true,
            Visible = false
        };

        var subViewSubView1 = new View
        {
            Id = "subViewSubView1",
            CanFocus = false
        };

        var subViewSubView2 = new View
        {
            Id = "subViewSubView2",
            CanFocus = true // This is the one that will be focused
        };

        var subViewSubView3 = new View
        {
            Id = "subViewSubView3",
            CanFocus = true
        };
        subView.Add (subViewSubView1, subViewSubView2, subViewSubView3);

        view.Add (subView);

        view.SetFocus ();
        Assert.False (subView.HasFocus);
        Assert.False (subViewSubView2.HasFocus);

        subView.Visible = true;
        Assert.True (subView.HasFocus);
        Assert.Equal (subView, view.GetFocused ());
        Assert.Equal (subViewSubView2, subView.GetFocused ());
        Assert.True (subViewSubView2.HasFocus);
    }
}
