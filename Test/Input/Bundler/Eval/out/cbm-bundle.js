!function(n) {
    "use strict";
    var t, e, a, r, i, _;
    t = Object.setPrototypeOf || {
        __proto__: []
    } instanceof Array && function(n, t) {
        n.__proto__ = t;
    } || function(n, t) {
        var e;
        for (e in t) if (t.hasOwnProperty(e)) n[e] = t[e];
    };
    e = Object.assign || function(n) {
        var t, e, a, r;
        for (t = 1, e = arguments.length; t < e; t++) {
            a = arguments[t];
            for (r in a) if (Object.prototype.hasOwnProperty.call(a, r)) n[r] = a[r];
        }
        return n;
    };
    a;
    r;
    i;
    _ = !1;
    function o() {
        eval("return 1");
    }
    function s(n) {
        return n + o();
    }
    console.log(s("a"));
}();

